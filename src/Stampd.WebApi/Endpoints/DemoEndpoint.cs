using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;

using PdfSharp.Drawing;
using PdfSharp.Pdf;

using Stampd.Core;
using Stampd.Core.Entities;
using Stampd.Core.Storage;
using Stampd.Core.Tenancy;
using Stampd.Infrastructure;
using Stampd.WebApi.Models;
using Stampd.WebApi.Services;

namespace Stampd.WebApi.Endpoints;

/// <summary>
/// Development-only one-click demo bootstrap. Ensures a sample NDA template exists
/// (creates if missing), then dispatches a fresh signing request to a hard-coded
/// recipient and returns the recipient's access URL. The Stampd UI's <c>/demo</c> page
/// calls this and immediately opens the URL in a new tab — the seamless E2E walk-through
/// the README promises.
/// </summary>
internal static class DemoEndpoint
{
    private const string DemoTemplateName = "Demo NDA";
    private const string DemoRoleName = "Signer";

    public static IEndpointRouteBuilder MapDemo(this IEndpointRouteBuilder builder, IWebHostEnvironment env)
    {
        // Only wired in Development. In any non-dev environment the route doesn't exist.
        if (!env.IsDevelopment())
        {
            return builder;
        }

        var group = builder.MapGroup("/api/demo").WithTags("Demo");

        group.MapPost("/seed", SeedAsync)
            .WithName("DemoSeed")
            .WithSummary("DEV-ONLY. Ensures a sample NDA template exists, dispatches a fresh signing request to a demo recipient, and returns the recipient access URL. Open the URL to walk through the signer flow.")
            .AllowAnonymous();

        return builder;
    }

    private static async Task<IResult> SeedAsync(
        [Microsoft.AspNetCore.Mvc.FromServices] StampdDbContext db,
        [Microsoft.AspNetCore.Mvc.FromServices] IDocumentStorageProvider storage,
        [Microsoft.AspNetCore.Mvc.FromServices] SigningWorkflowService workflow,
        [Microsoft.AspNetCore.Mvc.FromServices] WorkflowEmailOptions emailOptions,
        [Microsoft.AspNetCore.Mvc.FromServices] ITenantContext tenant,
        HttpContext http,
        CancellationToken ct)
    {
        // ---- 1. Ensure the demo template exists ----
        var existing = await db.DocumentTemplates
            .Include(t => t.Roles)
            .FirstOrDefaultAsync(t => t.Name == DemoTemplateName, ct)
            .ConfigureAwait(false);

        var template = existing ?? await CreateDemoTemplateAsync(db, storage, ct).ConfigureAwait(false);

        // ---- 2. Dispatch a signing request to a demo recipient ----
        // SenderEmail/SenderName populated so the completion notifier fires when the
        // demo recipient finishes signing.
        var body = new CreateSigningRequestBody(
            DocumentTemplateId: template.Id,
            Subject: $"Please sign — {DemoTemplateName}",
            Message: "This is a demo signing request seeded by Stampd's /api/demo/seed endpoint.",
            ExpiresAtUtc: null,
            Recipients:
            [
                new RecipientAssignment(
                    RoleName: DemoRoleName,
                    Email: "alice@example.com",
                    Name: "Alice Example"),
            ],
            SenderEmail: "sender@stampd.dev",
            SenderName: "Demo Sender");

        var created = await workflow.DispatchAsync(body, ct).ConfigureAwait(false);

        // Re-fetch with recipients so we can compose the access URL.
        var withRecipients = await db.SigningRequests
            .Include(r => r.Recipients)
            .FirstAsync(r => r.Id == created.Id, ct)
            .ConfigureAwait(false);

        var recipient = withRecipients.Recipients.First();
        var accessUrl = ComposeAccessUrl(emailOptions, http, recipient.AccessToken);

        return Results.Ok(new
        {
            templateId = template.Id,
            templateName = template.Name,
            signingRequestId = created.Id,
            recipientName = recipient.Name,
            recipientEmail = recipient.Email,
            accessUrl,
            otpRequired = template.Roles.First(r => r.Name == DemoRoleName).RequiresIdentityVerification,
        });
    }

    private static async Task<DocumentTemplate> CreateDemoTemplateAsync(
        StampdDbContext db,
        IDocumentStorageProvider storage,
        CancellationToken ct)
    {
        // Build a one-page synthetic NDA PDF inline.
        var pdfBytes = BuildDemoNdaPdf();
        var sha = Convert.ToHexString(SHA256.HashData(pdfBytes)).ToLowerInvariant();

        var storageKey = await storage.StoreAsync(
            pdfBytes,
            logicalName: $"template-{DemoTemplateName}.pdf",
            cancellationToken: ct).ConfigureAwait(false);

        var role = new TemplateRecipientRole
        {
            Id = Guid.NewGuid(),
            Name = DemoRoleName,
            RoutingOrder = 1,
            // Email-OTP gate ON so the seamless demo exercises the verification flow.
            RequiresIdentityVerification = true,
        };

        var field = new TemplateField
        {
            Id = Guid.NewGuid(),
            AssignedRole = role,
            AssignedRoleId = role.Id,
            PageNumber = 1,
            // Position a signature box near the bottom-left of the page.
            BoundsX = 8,
            BoundsY = 78,
            BoundsWidth = 30,
            BoundsHeight = 6,
            Kind = SignatureFieldKind.Signature,
            IsRequired = true,
            Label = "Signer signature",
        };

        var template = new DocumentTemplate
        {
            Name = DemoTemplateName,
            Description = "Seeded by /api/demo/seed. Replace or extend in the designer.",
            SourcePdfStorageKey = storageKey,
            SourcePdfSha256 = sha,
            CreatedBy = "demo",
            Roles = { role },
            Fields = { field },
        };

        db.DocumentTemplates.Add(template);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return template;
    }

    private static byte[] BuildDemoNdaPdf()
    {
        using var document = new PdfDocument();
        document.Info.Title = "Stampd Demo NDA";
        document.Info.Author = "Stampd";
        document.Info.Subject = "Seamless demo signing document";

        var page = document.AddPage();
        page.Size = PdfSharp.PageSize.Letter;

        using var gfx = XGraphics.FromPdfPage(page);
        var titleFont = new XFont("Helvetica", 22, XFontStyleEx.Bold);
        var headingFont = new XFont("Helvetica", 13, XFontStyleEx.Bold);
        var bodyFont = new XFont("Helvetica", 10.5, XFontStyleEx.Regular);

        gfx.DrawString(
            "Mutual Non-Disclosure Agreement",
            titleFont,
            XBrushes.Black,
            new XRect(0, 56, page.Width.Point, 36),
            XStringFormats.TopCenter);

        gfx.DrawString(
            "(Stampd seamless-demo sample — not legal advice)",
            new XFont("Helvetica", 9, XFontStyleEx.Italic),
            XBrushes.Gray,
            new XRect(0, 92, page.Width.Point, 18),
            XStringFormats.TopCenter);

        var leftMargin = 72;
        var rightMargin = 72;
        var contentWidth = page.Width.Point - leftMargin - rightMargin;
        var cursorY = 140d;

        DrawSection(
            "1. Confidential Information",
            "The parties may exchange technical, business, or product information that is confidential to the discloser. Each party agrees to keep such information confidential and to use it only for the purpose of evaluating a potential business relationship.");

        DrawSection(
            "2. Exclusions",
            "This agreement does not cover information that is publicly available, already known to the receiver, or independently developed without reference to the disclosed information.");

        DrawSection(
            "3. Term",
            "Obligations under this agreement remain in effect for two (2) years from the date of signature.");

        DrawSection(
            "4. No License",
            "Nothing in this agreement grants either party any license or other right to the other party's intellectual property except as expressly stated here.");

        cursorY += 24;
        gfx.DrawString(
            "Signature",
            headingFont,
            XBrushes.Black,
            new XRect(leftMargin, cursorY, contentWidth, 18),
            XStringFormats.TopLeft);

        return Save();

        void DrawSection(string heading, string body)
        {
            gfx.DrawString(
                heading,
                headingFont,
                XBrushes.Black,
                new XRect(leftMargin, cursorY, contentWidth, 18),
                XStringFormats.TopLeft);
            cursorY += 18;

            gfx.DrawString(
                body,
                bodyFont,
                XBrushes.Black,
                new XRect(leftMargin, cursorY, contentWidth, 80),
                XStringFormats.TopLeft);
            cursorY += 64;
        }

        byte[] Save()
        {
            using var ms = new MemoryStream();
            document.Save(ms);
            return ms.ToArray();
        }
    }

    private static string ComposeAccessUrl(WorkflowEmailOptions opts, HttpContext http, string accessToken)
    {
        if (!string.IsNullOrWhiteSpace(opts.SigningUrlTemplate))
        {
            return opts.SigningUrlTemplate!.Replace("{accessToken}", accessToken, StringComparison.Ordinal);
        }
        var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
        return $"{baseUrl}/api/sign/{accessToken}";
    }
}
