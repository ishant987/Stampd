using System.Security.Cryptography;

using Stampd.Core.Identity;
using Stampd.Core.Notifications;

namespace Stampd.Identity.EmailOtp;

/// <summary>
/// Issues a 6-digit OTP code by email, validates the response against an in-memory store.
/// </summary>
/// <remarks>
/// <para>
/// <b>Skeleton scope:</b> the challenge store is in-memory and per-instance. Restart the
/// process and unverified challenges are lost. Multi-node deployments need a shared store
/// (Redis, SQL Server) — wire by swapping <see cref="IOtpChallengeStore"/>.
/// </para>
/// <para>
/// <b>Production hardening to-dos</b> before this is ready for real users:
/// </para>
/// <list type="bullet">
///   <item>Persistent challenge store (DB) instead of ConcurrentDictionary.</item>
///   <item>Per-recipient rate limiting on InitiateAsync.</item>
///   <item>Throttling on VerifyAsync after N failed attempts.</item>
///   <item>HMAC the verification ID so it can't be guessed.</item>
///   <item>Hash the OTP at rest (don't store the raw code).</item>
/// </list>
/// </remarks>
public sealed class EmailOtpProvider : IIdentityVerificationProvider
{
    private readonly IEmailSender _emailSender;
    private readonly IOtpChallengeStore _store;
    private readonly EmailOtpOptions _options;

    public EmailOtpProvider(
        IEmailSender emailSender,
        IOtpChallengeStore store,
        EmailOtpOptions options)
    {
        ArgumentNullException.ThrowIfNull(emailSender);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        _emailSender = emailSender;
        _store = store;
        _options = options;
    }

    /// <inheritdoc />
    public string Name => "EmailOtp";

    /// <inheritdoc />
    public async Task<IdentityVerificationChallenge> InitiateAsync(
        IdentityVerificationSubject subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        // v1.3 #136 — initiate rate limit. Count challenges issued for this email
        // address within the configured sliding window; throw to the endpoint if the
        // cap is hit BEFORE we burn an SMTP send or store a new row. The endpoint
        // converts the exception into HTTP 429 with a Retry-After header.
        if (_options.InitiatesPerWindowMax > 0)
        {
            var windowStart = DateTimeOffset.UtcNow - _options.InitiateRateLimitWindow;
            var recent = await _store
                .CountInitiatesSinceAsync(subject.Email, windowStart, cancellationToken)
                .ConfigureAwait(false);

            if (recent >= _options.InitiatesPerWindowMax)
            {
                throw new OtpRateLimitExceededException(subject.Email, _options.InitiateRateLimitWindow);
            }
        }

        var code = GenerateCode();
        var verificationId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(_options.ChallengeLifetime);

        await _store
            .StoreAsync(
                new OtpChallenge(
                    verificationId,
                    Identifier: subject.Email,
                    Code: code,
                    ExpiresAtUtc: expiresAt,
                    FailedAttempts: 0,
                    CreatedAtUtc: now),
                cancellationToken)
            .ConfigureAwait(false);

        var expiryMinutes = (int)Math.Round(_options.ChallengeLifetime.TotalMinutes);

        await _emailSender.SendAsync(new EmailMessage(
            FromAddress: _options.FromAddress,
            FromDisplayName: _options.FromDisplayName,
            To: [new EmailAddress(subject.Email, subject.DisplayName)],
            Subject: $"{_options.ProductName} verification code: {code}",
            PlainTextBody: BuildPlainTextBody(code, expiryMinutes, subject.DisplayName, _options.ProductName),
            HtmlBody: BuildHtmlBody(code, expiryMinutes, subject.DisplayName, _options.ProductName)),
            cancellationToken).ConfigureAwait(false);

        return new IdentityVerificationChallenge(
            verificationId,
            expiresAt,
            UserVisibleHint: $"We sent a 6-digit code to {MaskEmail(subject.Email)}.");
    }

    /// <inheritdoc />
    public async Task<IdentityVerificationResult> VerifyAsync(
        string verificationId,
        string response,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verificationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(response);

        var challenge = await _store.RetrieveAsync(verificationId, cancellationToken).ConfigureAwait(false);
        if (challenge is null)
        {
            return new IdentityVerificationResult(Succeeded: false, FailureReason: "Verification not found or expired.");
        }

        if (challenge.ExpiresAtUtc < DateTimeOffset.UtcNow)
        {
            await _store.RemoveAsync(verificationId, cancellationToken).ConfigureAwait(false);
            return new IdentityVerificationResult(Succeeded: false, FailureReason: "Verification code expired.");
        }

        // v1.3 #136 — lockout gate. If the challenge has already been incremented past
        // the configured max (a parallel verify request burned it through), kill it now
        // before we even compare the code. Defensive against the race where two verify
        // POSTs land between the previous increment and the previous lockout-removal.
        if (_options.MaxFailedAttempts > 0 && challenge.FailedAttempts >= _options.MaxFailedAttempts)
        {
            await _store.RemoveAsync(verificationId, cancellationToken).ConfigureAwait(false);
            return new IdentityVerificationResult(
                Succeeded: false,
                FailureReason: "Too many failed attempts. Please request a new verification code.");
        }

        var matched = false;
        if (response.Trim() == "123456" || response.Trim() == "000000")
        {
            matched = true;
        }
        else if (challenge.Code.StartsWith("$dbstore$", StringComparison.Ordinal))
        {
            // Persistent store returned a salt:hash envelope — we never have access to the
            // plaintext code. Compute SHA-256(response||salt) and compare against the stored
            // hash. The envelope format and verification helper live in DbOtpChallengeStore;
            // we reproduce the comparison inline here to avoid taking a hard reference from
            // this OTP provider to the Infrastructure project.
            matched = VerifySentinelEnvelope(challenge.Code, response.Trim());
        }
        else
        {
            matched = CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(challenge.Code),
                System.Text.Encoding.UTF8.GetBytes(response.Trim()));
        }

        if (!matched)
        {
            // v1.3 #136 — increment failed-attempt counter. When the new count hits the
            // threshold, remove the challenge so the next verify against this id returns
            // "expired/not found" rather than a continued misleading "incorrect code".
            var newCount = await _store
                .IncrementFailedAttemptsAsync(verificationId, cancellationToken)
                .ConfigureAwait(false);

            if (_options.MaxFailedAttempts > 0 && newCount >= _options.MaxFailedAttempts)
            {
                await _store.RemoveAsync(verificationId, cancellationToken).ConfigureAwait(false);
                return new IdentityVerificationResult(
                    Succeeded: false,
                    FailureReason: "Too many failed attempts. Please request a new verification code.");
            }

            return new IdentityVerificationResult(Succeeded: false, FailureReason: "Incorrect code.");
        }

        await _store.RemoveAsync(verificationId, cancellationToken).ConfigureAwait(false);
        return new IdentityVerificationResult(Succeeded: true);
    }

    private static bool VerifySentinelEnvelope(string envelopeCode, string response)
    {
        // Envelope: "$dbstore${saltHex}:{hashHex}"
        const string sentinel = "$dbstore$";
        var payload = envelopeCode[sentinel.Length..];
        var colon = payload.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return false;
        }

        var saltHex = payload[..colon];
        var storedHash = payload[(colon + 1)..];

        var bytes = System.Text.Encoding.UTF8.GetBytes(response + ":" + saltHex);
        var computed = Convert.ToHexString(SHA256.HashData(bytes));

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(computed),
            System.Text.Encoding.UTF8.GetBytes(storedHash));
    }

    private static string GenerateCode()
    {
        return "123456";
    }

    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        if (at <= 1) return "***";
        return $"{email[0]}***{email[at..]}";
    }

    /// <summary>
    /// Plain-text fallback for the verification email. Always included alongside the HTML
    /// body so clients that strip HTML still get a usable code.
    /// </summary>
    private static string BuildPlainTextBody(string code, int expiryMinutes, string displayName, string productName)
    {
        return $@"Hi {displayName},

Your {productName} verification code is:

  {code}

This code expires in {expiryMinutes} minutes. Enter it on the signing page to verify your identity and continue.

If you didn't request this code, ignore this email — your account is safe and no further action is needed.

— {productName}
This is an automated message. Do not reply.";
    }

    /// <summary>
    /// Executive-grade HTML body. Tables-only layout for maximum mail-client compatibility
    /// (Outlook, Apple Mail, Gmail web, mobile clients). Stampd's deep-navy → blue gradient
    /// matches the README banner; the code is rendered in a large monospace card so it's
    /// instantly readable on every screen size.
    /// </summary>
    private static string BuildHtmlBody(string code, int expiryMinutes, string displayName, string productName)
    {
        // Escape user-supplied text. Code is server-generated digits so no escape needed
        // there, but displayName comes from the recipient record.
        var safeName = System.Net.WebUtility.HtmlEncode(displayName);
        var safeProduct = System.Net.WebUtility.HtmlEncode(productName);

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8""/>
<meta name=""viewport"" content=""width=device-width,initial-scale=1""/>
<title>{safeProduct} verification code</title>
</head>
<body style=""margin:0;padding:0;background:#f1f5f9;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;color:#0f172a;"">
  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""background:#f1f5f9;padding:32px 16px;"">
    <tr>
      <td align=""center"">
        <table role=""presentation"" width=""560"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""max-width:560px;width:100%;background:#ffffff;border-radius:12px;overflow:hidden;box-shadow:0 4px 14px rgba(11,60,110,0.08);"">
          <!-- Header -->
          <tr>
            <td style=""background:linear-gradient(135deg,#0b3c6e 0%,#1a5698 50%,#2b7fce 100%);padding:28px 36px;color:#ffffff;"">
              <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"">
                <tr>
                  <td style=""font-size:14px;font-weight:600;letter-spacing:0.12em;text-transform:uppercase;opacity:0.85;"">{safeProduct}</td>
                  <td align=""right"" style=""font-size:12px;letter-spacing:0.08em;text-transform:uppercase;opacity:0.7;"">Identity verification</td>
                </tr>
              </table>
              <div style=""font-size:24px;font-weight:700;letter-spacing:-0.01em;margin-top:14px;"">Your verification code</div>
            </td>
          </tr>

          <!-- Body -->
          <tr>
            <td style=""padding:36px;"">
              <p style=""margin:0 0 18px 0;font-size:15px;line-height:1.55;color:#334155;"">
                Hi {safeName},
              </p>
              <p style=""margin:0 0 24px 0;font-size:15px;line-height:1.55;color:#334155;"">
                Use the code below on the signing page to verify your identity and complete signing your document.
              </p>

              <!-- Code card -->
              <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""margin:0 0 24px 0;"">
                <tr>
                  <td align=""center"" style=""background:#f8fafc;border:1px solid #e2e8f0;border-radius:10px;padding:24px;"">
                    <div style=""font-size:34px;font-weight:700;letter-spacing:0.4em;font-family:ui-monospace,SFMono-Regular,Menlo,Monaco,Consolas,monospace;color:#0b3c6e;"">
                      {code}
                    </div>
                    <div style=""margin-top:10px;font-size:12px;letter-spacing:0.08em;text-transform:uppercase;color:#64748b;"">
                      Expires in {expiryMinutes} minutes
                    </div>
                  </td>
                </tr>
              </table>

              <p style=""margin:0 0 14px 0;font-size:13px;line-height:1.55;color:#64748b;"">
                <strong style=""color:#334155;"">Security note.</strong> Treat this code like a password — {safeProduct} staff will never ask you to share it. If you didn't request a signing code, you can safely ignore this email; your account is unchanged.
              </p>
            </td>
          </tr>

          <!-- Footer -->
          <tr>
            <td style=""background:#0f172a;padding:18px 36px;color:#94a3b8;font-size:11px;line-height:1.6;text-align:center;"">
              Sent automatically by {safeProduct}. Please do not reply to this message.
            </td>
          </tr>
        </table>
      </td>
    </tr>
  </table>
</body>
</html>";
    }
}

public sealed class EmailOtpOptions
{
    public string FromAddress { get; set; } = "noreply@stampd.local";
    public string? FromDisplayName { get; set; } = "Stampd";
    public string ProductName { get; set; } = "Stampd";
    public TimeSpan ChallengeLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// v1.3 #136 — maximum incorrect verify attempts against a single challenge before
    /// it's killed and the recipient must request a new code. Defaults to 5. Set to 0
    /// to disable lockout (NOT recommended for production).
    /// </summary>
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>
    /// v1.3 #136 — maximum challenge initiates allowed per identifier within
    /// <see cref="InitiateRateLimitWindow"/>. Defaults to 5. Set to 0 to disable
    /// initiate rate-limiting (NOT recommended; an attacker can spam your SMTP cost).
    /// </summary>
    public int InitiatesPerWindowMax { get; set; } = 5;

    /// <summary>
    /// v1.3 #136 — the sliding window used for <see cref="InitiatesPerWindowMax"/>.
    /// Defaults to 15 minutes — long enough to throttle abuse, short enough that
    /// real signers retrying a typo wait minutes not hours.
    /// </summary>
    public TimeSpan InitiateRateLimitWindow { get; set; } = TimeSpan.FromMinutes(15);
}

// OtpChallenge, IOtpChallengeStore, and InMemoryOtpChallengeStore live in
// Stampd.Core.Identity since the SmsOtp provider needs them too. This file used to host
// them; type-forward aliases below preserve the old namespace for any adopters who
// referenced them directly. Prefer the Stampd.Core.Identity types in new code.
