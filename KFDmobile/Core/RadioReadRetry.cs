namespace KfdMobile;
public static class RadioReadRetry
{
    // Only idempotent radio reads may use this helper. Never retry writes/erase.
    public static T Run<T>(Func<T> read, Action<int> retry = null)
    {
        for(int attempt=1;;attempt++)
        {
            try{return read();}
            catch(Exception ex) when(attempt<3 && (ex is TimeoutException || ex.Message.StartsWith("Radio handshake failed:",StringComparison.Ordinal) || ex.Message=="mr: unexpected opcode"))
            { if(retry!=null)retry(attempt+1);else Thread.Sleep(250); }
        }
    }
}
