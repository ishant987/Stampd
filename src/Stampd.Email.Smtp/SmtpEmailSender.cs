using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging;
using MimeKit;

using Stampd.Core.Notifications;

namespace Stampd.Email.Smtp;

/// <summary>
/// MailKit-backed SMTP <see cref="IEmailSender"/>. Suitable for any RFC 5321 server.
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly SmtpEmailSenderOptions _options;
    private readonly ILogger<SmtpEmailSender>? _logger;

    public SmtpEmailSender(SmtpEmailSenderOptions options, ILogger<SmtpEmailSender>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => $"Smtp({_options.Host}:{_options.Port})";

    /// <inheritdoc />
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(message.FromDisplayName ?? message.FromAddress, message.FromAddress));
        foreach (var to in message.To)
        {
            mime.To.Add(new MailboxAddress(to.DisplayName ?? to.Address, to.Address));
        }

        mime.Subject = message.Subject;

        var body = new BodyBuilder
        {
            TextBody = message.PlainTextBody,
            HtmlBody = message.HtmlBody,
        };

        if (message.Attachments is { Count: > 0 })
        {
            foreach (var att in message.Attachments)
            {
                body.Attachments.Add(att.FileName, att.Content.ToArray(), ContentType.Parse(att.ContentType));
            }
        }

        mime.Body = body.ToMessageBody();

        try
        {
            using var client = new SmtpClient();
            await client
                .ConnectAsync(_options.Host!, _options.Port, _options.Security, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrEmpty(_options.Username))
            {
                await client
                    .AuthenticateAsync(_options.Username, _options.Password ?? string.Empty, cancellationToken)
                    .ConfigureAwait(false);
            }

            await client.SendAsync(mime, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not deliver email over SMTP ({Host}:{Port}). Subject: '{Subject}'. Recipient: {Recipients}",
                _options.Host, _options.Port, message.Subject, string.Join(", ", message.To.Select(t => t.Address)));
            _logger?.LogInformation("Email notification fallback (logged only):\n{PlainText}", message.PlainTextBody);
        }
    }
}
