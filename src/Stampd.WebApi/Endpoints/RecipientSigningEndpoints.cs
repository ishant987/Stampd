using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Stampd.Core.Entities;
using Stampd.Core.Identity;
using Stampd.Core.Storage;
using Stampd.Infrastructure;
using Stampd.WebApi.Models;
using Stampd.WebApi.Services;

namespace Stampd.WebApi.Endpoints;

internal static class RecipientSigningEndpoints
{
    public static IEndpointRouteBuilder MapRecipientSigning(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/sign")
            .WithTags("Recipient")
            .AllowAnonymous();

        group.MapGet("/{accessToken}", GetAsync)
            .WithName("RecipientGetSigningView")
            .WithSummary("Recipient-facing: returns the fields this signer needs to fill. Token is single-use; rotated on resend.");

        group.MapPost("/{accessToken}", SubmitAsync)
            .WithName("RecipientSubmitSignature")
            .WithSummary("Recipient-facing: submit field values. When all required recipients have signed, the document is finalized in the same call.");

        group.MapGet("/{accessToken}/document", DownloadSourceAsync)
            .WithName("RecipientDownloadSourceDocument")
            .WithSummary("Recipient-facing: streams the unsigned source PDF so the signer can read what they're signing. Only available while the recipient is in Invited or Viewed state.");

        group.MapPost("/{accessToken}/initiate-verification", InitiateVerificationAsync)
            .WithName("RecipientInitiateIdentityVerification")
            .WithSummary("Recipient-facing: sends an identity-verification challenge (Email OTP). Returns the verification id and code expiry. Only valid when the recipient's role has RequiresIdentityVerification set.");

        group.MapPost("/{accessToken}/verify-identity", VerifyIdentityAsync)
            .WithName("RecipientVerifyIdentity")
            .WithSummary("Recipient-facing: verifies the OTP code. On success, marks the recipient as identity-verified so the signing form unlocks.");

        group.MapGet("/{accessToken}/signed-document", DownloadSignedAsync)
            .WithName("RecipientDownloadSignedDocument")
            .WithSummary("Recipient-facing: streams the sealed PDF back to the signer once the document is fully signed. 409 if the workflow isn't complete yet.");

        return builder;
    }

    private static async Task<IResult> DownloadSourceAsync(
        string accessToken,
        [FromServices] SigningWorkflowService workflow,
        [FromServices] IDocumentStorageProvider storage,
        CancellationToken ct)
    {
        var pair = await workflow.ResolveByAccessTokenAsync(accessToken, ct).ConfigureAwait(false);
        if (pair is null)
        {
            return Results.NotFound();
        }

        var (request, recipient) = pair.Value;

        // Recipient must be in a state where viewing the document is meaningful. We don't
        // serve the unsigned PDF once they've already signed, declined, or expired — that
        // path is reserved for /api/signed-documents/{id}/download with proper auth.
        if (recipient.Status is RecipientStatus.Signed or RecipientStatus.Declined or RecipientStatus.Expired)
        {
            return Results.Problem(
                $"This signing link is in terminal state {recipient.Status} and the source document is no longer available here.",
                statusCode: 409);
        }

        if (recipient.Status == RecipientStatus.Pending)
        {
            return Results.Problem(
                "It's not your turn to sign yet — earlier recipients in the routing order must finish first.",
                statusCode: 409);
        }

        var template = request.DocumentTemplate!;
        var bytes = await storage.RetrieveAsync(template.SourcePdfStorageKey, ct).ConfigureAwait(false);

        return Results.File(
            fileContents: bytes,
            contentType: "application/pdf",
            fileDownloadName: $"template-{template.Id:N}.pdf");
    }

    private static async Task<IResult> GetAsync(
        string accessToken,
        [FromServices] SigningWorkflowService workflow,
        CancellationToken ct)
    {
        var pair = await workflow.ResolveByAccessTokenAsync(accessToken, ct).ConfigureAwait(false);
        if (pair is null)
        {
            return Results.NotFound();
        }

        var (request, recipient) = pair.Value;

        if (recipient.Status is RecipientStatus.Signed or RecipientStatus.Declined or RecipientStatus.Expired)
        {
            return Results.Ok(BuildView(request, recipient, fields: []));
        }

        if (recipient.Status == RecipientStatus.Pending)
        {
            return Results.Problem(
                "It's not your turn to sign yet — earlier recipients in the routing order must finish first.",
                statusCode: 409);
        }

        await workflow.MarkViewedAsync(recipient, ct).ConfigureAwait(false);

        // Surface only the fields assigned to this recipient's role (plus any unassigned/global fields).
        var template = request.DocumentTemplate!;
        var ordered = template.Fields
            .OrderBy(f => f.PageNumber)
            .ThenBy(f => f.BoundsY)
            .ThenBy(f => f.BoundsX)
            .ToList();

        var fields = ordered
            .Select((f, idx) => new RecipientFieldView(
                Index: idx,
                PageNumber: f.PageNumber,
                Bounds: new ApiPercentageRect(f.BoundsX, f.BoundsY, f.BoundsWidth, f.BoundsHeight),
                Kind: f.Kind,
                Label: f.Label,
                IsRequired: f.IsRequired))
            .Where(f => IsForRecipient(ordered[f.Index], recipient))
            .ToList();

        return Results.Ok(BuildView(request, recipient, fields));
    }

    private static async Task<IResult> SubmitAsync(
        string accessToken,
        [FromBody] SubmitRecipientSignatureRequest body,
        [FromServices] SigningWorkflowService workflow,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        var pair = await workflow.ResolveByAccessTokenAsync(accessToken, ct).ConfigureAwait(false);
        if (pair is null)
        {
            return Results.NotFound();
        }

        var (request, recipient) = pair.Value;

        if (recipient.Status == RecipientStatus.Signed)
        {
            return Results.Problem("You have already signed this document.", statusCode: 409);
        }

        if (recipient.Status == RecipientStatus.Pending)
        {
            return Results.Problem("It's not your turn to sign yet.", statusCode: 409);
        }

        if (request.Status is SigningRequestStatus.Declined or SigningRequestStatus.Voided or SigningRequestStatus.Expired)
        {
            return Results.Problem(
                $"This signing request is in terminal state {request.Status} and can no longer accept signatures.",
                statusCode: 409);
        }

        // Identity-verification gate. If the recipient's role requires it and they haven't
        // completed the OTP challenge, refuse the submission. The UI gate is a usability
        // layer; this is the security layer.
        if (recipient.Role?.RequiresIdentityVerification == true
            && recipient.IdentityVerifiedAtUtc is null)
        {
            return Results.Problem(
                "Identity verification is required before you can sign this document.",
                statusCode: 403);
        }

        var (recipientStatus, workflowStatus, signedDocumentId, signedHash) =
            await workflow.SubmitAsync(recipient, body.FieldValues, ct).ConfigureAwait(false);

        return Results.Ok(new RecipientSubmitResponse(
            Status: recipientStatus,
            WorkflowStatus: workflowStatus,
            SignedDocumentId: signedDocumentId,
            SignedDocumentHashSha256: signedHash));
    }

    private static async Task<IResult> DownloadSignedAsync(
        string accessToken,
        [FromServices] SigningWorkflowService workflow,
        [FromServices] StampdDbContext db,
        [FromServices] IDocumentStorageProvider storage,
        CancellationToken ct)
    {
        var pair = await workflow.ResolveByAccessTokenAsync(accessToken, ct).ConfigureAwait(false);
        if (pair is null)
        {
            return Results.NotFound();
        }

        var (request, recipient) = pair.Value;

        // Only release the sealed PDF to the recipient once they've personally signed it.
        if (recipient.Status != RecipientStatus.Signed)
        {
            return Results.Problem(
                $"This signing link hasn't completed yet (recipient status: {recipient.Status}).",
                statusCode: 409);
        }

        // The signed PDF only exists after the workflow service has finalized — i.e., all
        // required recipients have signed and the engine has produced the sealed bytes.
        // v1.3 #133: server-side ORDER BY on the epoch shadow column. There's typically
        // exactly one record per signing request (SigningRequestId is unique on the
        // table), but ordering newest-first is the contractually correct behavior if a
        // re-sign workflow ever lands.
        var signed = await db.SignedDocumentRecords
            .Where(r => r.SigningRequestId == request.Id)
            .OrderByDescending(r => r.SignedAtUtcEpochMs)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (signed is null)
        {
            return Results.Problem(
                "You've signed, but the signing workflow is waiting on other recipients before the final sealed PDF is produced.",
                statusCode: 409);
        }

        var bytes = await storage.RetrieveAsync(signed.StorageKey, ct).ConfigureAwait(false);
        return Results.File(
            fileContents: bytes,
            contentType: "application/pdf",
            fileDownloadName: $"signed-{signed.Id:N}.pdf");
    }

    private static async Task<IResult> InitiateVerificationAsync(
        string accessToken,
        [FromServices] SigningWorkflowService workflow,
        [FromServices] IIdentityVerificationProvider verificationProvider,
        CancellationToken ct)
    {
        var pair = await workflow.ResolveByAccessTokenAsync(accessToken, ct).ConfigureAwait(false);
        if (pair is null)
        {
            return Results.NotFound();
        }

        var (request, recipient) = pair.Value;

        if (recipient.Role?.RequiresIdentityVerification != true)
        {
            return Results.Problem(
                "Identity verification is not required for this recipient.",
                statusCode: 409);
        }

        if (recipient.IdentityVerifiedAtUtc is not null)
        {
            return Results.Problem(
                "You have already verified your identity for this signing request.",
                statusCode: 409);
        }

        if (recipient.Status is RecipientStatus.Signed or RecipientStatus.Declined or RecipientStatus.Expired)
        {
            return Results.Problem(
                $"This signing link is in terminal state {recipient.Status}.",
                statusCode: 409);
        }

        if (recipient.Status == RecipientStatus.Pending)
        {
            return Results.Problem(
                "It's not your turn to sign yet — earlier recipients in the routing order must finish first.",
                statusCode: 409);
        }

        var subject = new IdentityVerificationSubject(
            Email: recipient.Email,
            DisplayName: recipient.Name);

        IdentityVerificationChallenge challenge;
        try
        {
            challenge = await verificationProvider.InitiateAsync(subject, ct).ConfigureAwait(false);
        }
        catch (Stampd.Core.Identity.OtpRateLimitExceededException ex)
        {
            // v1.3 #136 — convert rate-limit rejection into a standards-compliant 429.
            // Retry-After is RFC 7231 §7.1.3: integer seconds OR an HTTP-date. Seconds
            // is simpler and good enough for OTP throttling windows measured in
            // minutes. The body carries a recipient-friendly message; the Identifier
            // is deliberately NOT echoed back to avoid leaking which addresses are
            // valid recipients in this tenant.
            return Results.Problem(
                detail: "Too many verification requests for this signer. Please try again later.",
                statusCode: 429,
                extensions: new Dictionary<string, object?>
                {
                    ["retryAfterSeconds"] = (int)ex.RetryAfter.TotalSeconds,
                });
        }

        return Results.Ok(new InitiateVerificationResponse(
            VerificationId: challenge.VerificationId,
            ExpiresAtUtc: challenge.ExpiresAtUtc,
            UserVisibleHint: challenge.UserVisibleHint));
    }

    private static async Task<IResult> VerifyIdentityAsync(
        string accessToken,
        [FromBody] VerifyIdentityRequest body,
        [FromServices] SigningWorkflowService workflow,
        [FromServices] IIdentityVerificationProvider verificationProvider,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (string.IsNullOrWhiteSpace(body.VerificationId) || string.IsNullOrWhiteSpace(body.Code))
        {
            return Results.Problem("VerificationId and Code are required.", statusCode: 400);
        }

        var pair = await workflow.ResolveByAccessTokenAsync(accessToken, ct).ConfigureAwait(false);
        if (pair is null)
        {
            return Results.NotFound();
        }

        var (_, recipient) = pair.Value;

        if (recipient.Role?.RequiresIdentityVerification != true)
        {
            return Results.Problem(
                "Identity verification is not required for this recipient.",
                statusCode: 409);
        }

        if (recipient.IdentityVerifiedAtUtc is not null)
        {
            return Results.Ok(new VerifyIdentityResponse(
                Succeeded: true,
                FailureReason: null,
                IdentityVerifiedAtUtc: recipient.IdentityVerifiedAtUtc));
        }

        var result = await verificationProvider.VerifyAsync(body.VerificationId, body.Code, ct).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return Results.Ok(new VerifyIdentityResponse(
                Succeeded: false,
                FailureReason: result.FailureReason ?? "Verification failed.",
                IdentityVerifiedAtUtc: null));
        }

        var verifiedAt = await workflow
            .MarkIdentityVerifiedAsync(recipient, verificationProvider.Name, ct)
            .ConfigureAwait(false);

        return Results.Ok(new VerifyIdentityResponse(
            Succeeded: true,
            FailureReason: null,
            IdentityVerifiedAtUtc: verifiedAt));
    }

    private static bool IsForRecipient(TemplateField field, Recipient recipient)
    {
        // Unassigned fields are global (date stamps, company logos etc.) — show them too so
        // the recipient can see context, even though they shouldn't be filled.
        if (field.AssignedRoleId is null)
        {
            return true;
        }

        return field.AssignedRoleId == recipient.RoleId;
    }

    private static RecipientSigningView BuildView(
        SigningRequest request,
        Recipient recipient,
        IReadOnlyList<RecipientFieldView> fields)
        => new(
            request.Id,
            request.Subject,
            request.Message,
            recipient.Name,
            recipient.Email,
            recipient.Status,
            fields,
            RequiresIdentityVerification: recipient.Role?.RequiresIdentityVerification == true,
            IdentityVerifiedAtUtc: recipient.IdentityVerifiedAtUtc,
            IdentityVerificationMethod: recipient.IdentityVerificationMethod);
}
