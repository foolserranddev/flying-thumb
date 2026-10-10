namespace FlyingThumbManager;

public static class TransferError
{
    public static string Describe(Exception error)
    {
        if(error is OperationCanceledException)
            return "The request timed out before the drive completed it. " + error.Message;
        var messages=new List<string>();
        for(Exception? current=error;current is not null;current=current.InnerException)
            if(!string.IsNullOrWhiteSpace(current.Message) && !messages.Contains(current.Message)) messages.Add(current.Message);
        return string.Join(" Details: ",messages);
    }
}
