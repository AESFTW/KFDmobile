using Android.App;
using Android.Widget;
using KFDtool.P25.TransferConstructs;

namespace KfdMobile.AndroidApp;

public partial class MainActivity
{
    void RenderRadio()
    {
        Text(content, "Connected radio", 22);
        Text(content, "Connect the radio to your selected KFD. Radio operations change the radio, not the open container.", 14);
        Button(content, "Read radio key inventory / erase selected CKRs", ReadRadioInventory);
        Button(content, "Zeroize all radio keys", () => WithUsb(() => {
            var device = Device();
            Confirm("Zeroize all radio keys?", "Permanently erase ALL keys in the connected radio, including key-encryption keys. This cannot be undone.", () => RadioWork(device, "Zeroizing all radio keys", () => InteractTwiKfdtool.EraseAllKeys(device), () => Message("Radio zeroized", "The radio acknowledged erasing all keys.")));
        }));
        Button(content, "Read KMF RSI and radio RSI items", () => WithUsb(() => {
            var device = Device(); string result = "";
            Work("Reading radio RSI configuration", () => {
                int kmf = InteractTwiKfdtool.ViewKmfRsi(device); int period = InteractTwiKfdtool.ViewMnp(device);
                var items = InteractTwiKfdtool.ViewRsiItems(device);
                result = $"KMF RSI: {kmf} (0x{kmf:X6})\nMessage number period: {period}\n\n" + string.Join("\n", items.Select(i => $"Radio RSI: {i.RSI} (0x{i.RSI:X6}) · MN {i.MN}"));
            }, () => Message("Radio RSI configuration", result));
        }));
        Button(content, "View keysets", () => WithUsb(() =>
        {
            var device = Device(); string result = ""; keysetResult = "";
            Work("Reading keysets", () =>
            {
                var sets = RadioReadRetry.Run(() => InteractTwiKfdtool.ViewKeysetTaggingInfo(device));
                result = sets.Count == 0 ? "Radio returned no keysets." : string.Join("\n\n", sets.OrderBy(k => k.KeysetId).Select(k =>
                    $"Keyset {k.KeysetId}  ·  {k.KeysetType}  ·  {k.KeysetName}\nActivation: {(k.ActivationDateTime == DateTime.MinValue ? "Not specified" : k.ActivationDateTime.ToString("yyyy-MM-dd HH:mm:ss"))}"));
            }, () => Message("Radio keysets", result));
        }));
        Button(content, "Set radio KMF RSI", () => EditRadioRsi(false));
        Button(content, "Change radio RSI", () => EditRadioRsi(true));
    }
    void RadioWork(KFDtool.P25.TransferConstructs.BaseDevice device, string title, Action operation, Action done) => WithUsb(() => {
        if (device.TwiKfdtoolDevice.ComPort != usbName || device.KfdDeviceType != Device().KfdDeviceType)
            throw new InvalidOperationException("KFD selection changed. Read the radio again before continuing.");
        Work(title, operation, done);
    });
    void ReadRadioInventory() => WithUsb(() => {
        var device = Device(); List<RspKeyInfo> inventory = null;
        Work("Reading radio key inventory", () => inventory = RadioReadRetry.Run(() => InteractTwiKfdtool.ViewKeyInfo(device)), () => {
            var form = Column(); Text(form, "Select CKRs to erase from this radio. Keyset IDs are preserved when targeting each entry.", 14);
            var rows = inventory.OrderBy(k => k.Sln).ThenBy(k => k.KeysetId).ToList();
            var selectedRows = new List<(RspKeyInfo Key, CheckBox Check)>();
            if (rows.Count == 0) Text(form, "Radio returned no key entries.");
            Button(form, "Select all / clear", () => { bool select = selectedRows.Any(r => !r.Check.Checked); foreach(var row in selectedRows) row.Check.Checked = select; });
            foreach(var key in rows) selectedRows.Add((key, Check(form, $"CKR {key.Sln} · KID {KeyPresentation.Kid(key.KeyId, kidHex)}\nKeyset {key.KeysetId} · {KeyPresentation.Algorithm(key.AlgorithmId)}", false)));
            var scroll = new ScrollView(this); scroll.AddView(form);
            var dialog = new AlertDialog.Builder(this).SetTitle("Radio key inventory").SetView(scroll).SetNegativeButton("Close", (_, _) => { }).Create();
            Button(form, "Erase selected CKRs", () => {
                var chosen = selectedRows.Where(r => r.Check.Checked).Select(r => r.Key).ToList();
                if (chosen.Count == 0) throw new ArgumentException("Select at least one CKR to erase.");
                Confirm("Erase selected radio keys?", "This permanently removes these radio locations:\n" + string.Join("\n", chosen.Select(k => $"CKR {k.Sln} · keyset {k.KeysetId}")), () => {
                    dialog.Dismiss();
                    var commands = chosen.Select(k => new CmdKeyItem { Sln = k.Sln, KeysetId = k.KeysetId, UseActiveKeyset = false, IsKek = k.Sln >= 61440, AlgorithmId = k.AlgorithmId, KeyId = k.KeyId, Key = new() }).ToList();
                    RadioWork(device, "Erasing selected radio CKRs", () => InteractTwiKfdtool.EraseKey(device, commands), () => Message("Radio keys erased", $"The radio acknowledged erasing {commands.Count} selected entries. Read inventory again to refresh."));
                });
            }, rows.Count > 0);
            ShowDialog(dialog);
        });
    });
    void EditRadioRsi(bool change) => WithUsb(() => {
        var device = Device(); var form = Column();
        var old = change ? Field(form, "Current radio RSI (decimal)", "", numeric: true) : null;
        var value = Field(form, change ? "New radio RSI (decimal)" : "KMF RSI (decimal)", "", numeric: true);
        var number = Field(form, change ? "Message number (0–65535)" : "Message number period (0–65535)", "0", numeric: true);
        var dialog = new AlertDialog.Builder(this).SetTitle(change ? "Change radio RSI" : "Set radio KMF RSI").SetView(form).SetNegativeButton("Cancel", (_, _) => { }).Create();
        Button(form, "Review change", () => {
            int next = int.Parse(value.Text), mn = int.Parse(number.Text), previous = change ? int.Parse(old.Text) : 0;
            RadioValidation.Rsi(change, previous, next, mn);
            Confirm("Write radio RSI configuration?", change ? $"Change radio RSI {previous} to {next}, message number {mn}." : $"Set radio KMF RSI to {next}, message number period {mn}.", () => {
                dialog.Dismiss(); RspRsiInfo response = null;
                RadioWork(device, "Writing radio RSI configuration", () => {
                    response = change ? InteractTwiKfdtool.ChangeRsi(device, previous, next, mn) : InteractTwiKfdtool.LoadConfig(device, next, mn);
                    if (response.Status != 0) throw new IOException($"Radio did not apply the RSI change: {KFDtool.P25.Kmm.OperationStatusExtensions.ToStatusString((KFDtool.P25.Kmm.OperationStatus)response.Status)} (status {response.Status}).\n{KFDtool.P25.Kmm.OperationStatusExtensions.ToReasonString((KFDtool.P25.Kmm.OperationStatus)response.Status)}\nRead the radio RSI configuration and verify the current RSI before retrying.");
                }, () => Message("Radio RSI updated", $"RSI {response.RSI} · Message number {response.MN}"));
            });
        }); ShowDialog(dialog);
    });
}
