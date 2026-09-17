namespace Stampd.Core.Notifications;

/// <summary>
/// Sends transactional emails — signing invitations, OTP codes, completion notifications.
/// Pluggable so adopters can wire SMTP, SendGrid, SES, Postmark, or custom providers
/// without changing engine or workflow code.
/// </summary>
public interface IEmailSender
{
    /// <summary>Short identifying name recorded into the audit trail.</summary>
    string Name { get; }

    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>One email message. Bodies can be plain text, HTML, or both.</summary>
public sealed record EmailMessage(
    string FromAddress,
    string? FromDisplayName,
    IReadOnlyList<EmailAddress> To,
    string Subject,
    string? PlainTextBody,
    string? HtmlBody,
    IReadOnlyList<EmailAttachment>? Attachments = null);

public sealed record EmailAddress(string Address, string? DisplayName = null);

public sealed record EmailAttachment(
    string FileName,
    string ContentType,
    ReadOnlyMemory<byte> Content);
