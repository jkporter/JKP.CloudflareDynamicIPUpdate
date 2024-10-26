namespace JKP.CloudflareDynamicIPUpdate.Notification;

public interface INotifier
{
    Task SendNotification (CancellationToken cancellationToken = default);
}