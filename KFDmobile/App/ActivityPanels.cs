using Android.App;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using KFDtool.Container;

namespace KfdMobile.AndroidApp;
public partial class MainActivity
{
    readonly List<(DateTime Time, ReceivedMrKey Key)> receivedKeys = new();
    LinearLayout mrCards;
    void Separator(LinearLayout parent)
    {
        var line=new View(this);line.SetBackgroundColor(dark?Color.Rgb(60,70,80):Color.Rgb(210,216,222));
        parent.AddView(line,new LinearLayout.LayoutParams(-1,Dp(1)));
    }
    void ActivityList(LinearLayout parent, IEnumerable<string> entries)
    {
        var list=Column();foreach(var entry in entries.Reverse()){Text(list,entry,14);Separator(list);}
        var scroll=new ScrollView(this);scroll.AddView(list);parent.AddView(scroll,new LinearLayout.LayoutParams(-1,Dp(260)));
    }
    void RenderReceivedKeys()
    {
        if(mrCards==null)return;mrCards.RemoveAllViews();
        if(receivedKeys.Count==0)Text(mrCards,"Received keys will appear here.",14);
        foreach(var entry in receivedKeys.AsEnumerable().Reverse())
        {
            Text(mrCards,$"{entry.Time:HH:mm:ss} · {KeyPresentation.Algorithm(entry.Key.AlgorithmId)}",17).SetTypeface(null,TypefaceStyle.Bold);
            Text(mrCards,$"CKR {entry.Key.Sln} · KID {KeyPresentation.Kid(entry.Key.KeyId,kidHex)} · Keyset {entry.Key.KeysetId}",14);
            var value=Text(mrCards,new string('•',entry.Key.KeyHex.Length),14);value.SetTypeface(Typeface.Monospace,TypefaceStyle.Normal);value.SaveEnabled=false;
            var show=Check(mrCards,"Show received key",false);show.Enabled=true;
            show.CheckedChange+=(_,e)=>value.Text=e.IsChecked?entry.Key.KeyHex:new string('•',entry.Key.KeyHex.Length);
            Separator(mrCards);
        }
    }
    void ConfirmKeyLoad(List<KeyItem> keys, Action yes)
    {
        var body=Column();Text(body,$"Load {keys.Count} key(s) into the connected radio. Existing keys at these locations may be replaced.",14);
        foreach(var key in keys){Text(body,key.Name,19).SetTypeface(null,TypefaceStyle.Bold);Text(body,$"{KeyPresentation.Algorithm(key.AlgorithmId)}\nKID {KeyPresentation.Kid(key.KeyId,kidHex)} · CKR {key.Sln}\nKeyset: {(key.ActiveKeyset?"Active":key.KeysetId.ToString())}",14);Separator(body);}
        var scroll=new ScrollView(this);scroll.AddView(body);
        ShowDialog(new AlertDialog.Builder(this).SetTitle("Load keys?").SetView(scroll).SetNegativeButton("Cancel",(_,_)=>{}).SetPositiveButton("Load",(_,_)=>{try{yes();}catch(Exception ex){Error(ex);}}).Create());
    }
}
