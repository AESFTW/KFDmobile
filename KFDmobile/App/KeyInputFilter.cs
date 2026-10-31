using Android.Text;

namespace KfdMobile.AndroidApp;

sealed class KeyInputFilter(Func<int> algorithm, Action rejected) : Java.Lang.Object, IInputFilter
{
    public Java.Lang.ICharSequence FilterFormatted(Java.Lang.ICharSequence source, int start, int end, ISpanned dest, int dstart, int dend)
    {
        string inserted = source.ToString().Substring(start, end - start);
        if (KeyEntry.AcceptsReplacement(algorithm(), dest.ToString(), dstart, dend, inserted)) return null;
        rejected();
        // Reject the whole edit, preserving selected text instead of silently truncating a key.
        return new Java.Lang.String(dest.ToString().Substring(dstart, dend - dstart));
    }
}
