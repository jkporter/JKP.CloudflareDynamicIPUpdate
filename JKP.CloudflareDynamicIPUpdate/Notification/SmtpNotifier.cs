using MailKit.Net.Smtp;
using MimeKit;
using MimeKit.Text;

namespace JKP.CloudflareDynamicIPUpdate.Notification;

public class SmtpNotifier : INotifier
{
    public async Task SendNotification(CancellationToken cancellationToken =default)
    {
        using var client = new SmtpClient();
        await client.ConnectAsync ("smtp.friends.com", 587, false, cancellationToken);

        // Note: only needed if the SMTP server requires authentication
        await client.AuthenticateAsync ("joey", "password", cancellationToken);

        await client.SendAsync (CreateMessage(), cancellationToken);
        await client.DisconnectAsync (true, cancellationToken);
    }

    private MimeMessage CreateMessage()
    {
        var message = new MimeMessage ();
        message.From.Add (new MailboxAddress ("Jonathan Porter", "mail@j.porter.name"));
        message.To.Add (new MailboxAddress ("Jonathan Porter", "mail@j.porter.name"));
        message.Subject = "IP Change Notification";

        message.Body = new TextPart("plain")
        {
            Text = @""
        };

        message.Body = new TextPart(TextFormat.Html)
        {
            Text = @""
        };

        return message;
    }
}