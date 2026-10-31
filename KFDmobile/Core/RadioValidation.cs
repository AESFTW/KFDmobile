namespace KfdMobile;
public static class RadioValidation
{
    public static void Rsi(bool change, int previous, int next, int number)
    {
        if(number < 0 || number > 65535) throw new ArgumentException("Message number / period must be 0–65535.");
        if(!change) { if(next < 1 || next > 9999999) throw new ArgumentException("KMF RSI must be 1–9,999,999."); return; }
        bool Valid(int rsi) => rsi >= 1 && rsi <= 16777214 && rsi != 9999999;
        if(!Valid(previous) || !Valid(next)) throw new ArgumentException("Radio RSI must be 1–9,999,998 or 10,000,000–16,777,214.");
        if((previous < 9999999) != (next < 9999999)) throw new ArgumentException("An individual RSI cannot be changed to a group RSI, or vice versa.");
    }
}
