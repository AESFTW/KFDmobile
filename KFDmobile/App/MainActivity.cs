using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Usb;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using KFDtool.Adapter.Device;
using KFDtool.Adapter.Protocol.Adapter;
using KFDtool.Container;
using KFDtool.P25.TransferConstructs;
using System.Security.Cryptography;
using KeyItem = KFDtool.Container.KeyItem;
using Uri = Android.Net.Uri;

namespace KfdMobile.AndroidApp;

// Reopening from the launcher must reuse the Activity that owns the open container.
[Activity(Label = "KFD Mobile", MainLauncher = true, Exported = true, LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden | ConfigChanges.UiMode)]
public partial class MainActivity : Activity
{
    const string PermissionAction = "org.kfdmobile.android.USB_PERMISSION";
    readonly ContainerSession session = new();
    readonly HashSet<int> selected = new();
    readonly List<string> events = new();
    UsbManager usb;
    UsbReceiver receiver;
    UsbSerialTransport activeSerial;
    string usbName, status = "Ready", page = "Container";
    bool busy, pickerActive, shield, backgrounded, saveLocationCanceled, leftApp;
    DateTime backgroundTime;
    byte[] pendingExport, pendingDkf;
    Uri containerUri;
    string containerName = "keys.ekc";

    long revision;
    SaveStamp pendingStamp;
    Action afterSave;
    CancellationTokenSource autoSaveDelay;
    LinearLayout content;
    Button loadSelectedButton;
    ScrollView pageScroll;
    string renderedPage;
    bool restoringScroll;
    readonly Dictionary<string, int> scrollPositions = new();
    readonly Dictionary<int, View> keyRows = new();
    int? visibleKey;
    int visibleKeyOffset;
    bool kidHex;
    int keySort;
    readonly MrImportPolicy importPolicy = new();
    readonly List<Dialog> openDialogs = new();
    CancellationTokenSource backgroundLock, emulatorCancellation;
    string mrStatus = "", mrSaveStatus = "", keysetResult = "";
    TextView mrOutput, mrSaveOutput;
    readonly Queue<(ReceivedMrKey Key, InnerContainer Container)> importQueue = new();
    AlertDialog importDialog;
    bool dark, receiverRegistered, startupFailed;
    Bitmap headerLogo;
    string startupStage = "initializing";
    List<KFDtool.Adapter.Bundle.Update> firmwareUpdates = new();
    bool firmwareBusy;
    string firmwareStatus = "Choose a KFDtool .cfp or .ufp firmware package.";
    TextView firmwareOutput;

    Color Ink => dark ? Color.Rgb(235, 239, 246) : Color.Rgb(27, 43, 59);
    bool CloseContainerOnLeave => GetSharedPreferences("container", FileCreationMode.Private).GetBoolean("close_on_leave", true);
    string DisplayMode => GetSharedPreferences("appearance", FileCreationMode.Private).GetString("mode", "System");


    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        try
        {
        try { storage = StorageCredentials.Load(this); } catch { storageLoadError = "Saved storage settings could not be unlocked. Reconfigure Storage before opening or saving containers."; }
        kidHex = GetSharedPreferences("keys", FileCreationMode.Private).GetBoolean("hex", false);
        keySort = GetSharedPreferences("keys", FileCreationMode.Private).GetInt("sort", 0);
        shield = GetSharedPreferences("device", FileCreationMode.Private).GetBoolean("shield", false);
        Window.AddFlags(WindowManagerFlags.Secure);
        NLog.LogManager.Configuration = new NLog.Config.LoggingConfiguration();
        usb = (UsbManager)GetSystemService(UsbService);
        TransportRegistry.Factory = name =>
        {
            if (!usb.DeviceList.TryGetValue(name, out var device)) throw new IOException("USB keyloader is disconnected.");
            var transport = new UsbSerialTransport(usb, device);
            Interlocked.Exchange(ref activeSerial, transport)?.Dispose();
            return transport;
        };
        receiver = new UsbReceiver(this);
        var filter = new IntentFilter(PermissionAction);
        filter.AddAction(UsbManager.ActionUsbDeviceDetached);
        filter.AddAction(UsbManager.ActionUsbDeviceAttached);
        filter.AddAction(Intent.ActionScreenOff);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu) RegisterReceiver(receiver, filter, ReceiverFlags.NotExported);
        else RegisterReceiver(receiver, filter);
        receiverRegistered = true;
        Render();
        } catch (Exception ex) { ShowStartupFailure(ex); }
    }
    int Dp(int value) => (int)(value * Resources.DisplayMetrics.Density);
    LinearLayout Column() { var v = new LinearLayout(this) { Orientation = Orientation.Vertical }; v.SetPadding(Dp(16), Dp(8), Dp(16), Dp(8)); return v; }
    TextView Text(LinearLayout parent, string text, int size = 16)
    {
        var view = new TextView(this) { Text = text, TextSize = size }; view.SetTextColor(Ink); view.SetPadding(0, Dp(7), 0, Dp(7)); parent.AddView(view); return view;
    }
    Button Button(LinearLayout parent, string title, Action click, bool enabled = true)
    {
        var button = new Button(this) { Text = title, Enabled = enabled && !busy }; button.SetTextColor(Ink); button.SetAllCaps(false); button.SetMinHeight(Dp(48)); button.Click += (_, _) => { try { click(); } catch (Exception ex) { Error(ex); } }; parent.AddView(button); return button;
    }
    EditText Field(LinearLayout parent, string title, string value = "", bool secret = false, bool numeric = false)
    {
        Text(parent, title, 13);
        var field = new EditText(this) { Text = value, InputType = secret ? InputTypes.ClassText | InputTypes.TextVariationPassword : numeric ? InputTypes.ClassNumber : InputTypes.ClassText };
        field.SetTextColor(Ink); field.SetSingleLine(true); field.SetSelectAllOnFocus(true); field.SaveEnabled = false;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O) field.ImportantForAutofill = ImportantForAutofill.NoExcludeDescendants;
        parent.AddView(field); return field;
    }
    Spinner Choices(LinearLayout parent, string title, string[] values, int position = 0)
    {
        Text(parent, title, 13); var spinner = new Spinner(this);
        var adapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerItem, values); adapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
        spinner.Adapter = adapter; spinner.SetSelection(position); parent.AddView(spinner); return spinner;
    }
    CheckBox Check(LinearLayout parent, string title, bool value)
    {
        var check = new CheckBox(this) { Text = title, Checked = value, Enabled = !busy }; check.SetTextColor(Ink); parent.AddView(check); return check;
    }
    void Render()
    {
        if (startupFailed || IsDestroyed || IsFinishing) return;
        try { RenderPage(); } catch (Exception ex) { ShowStartupFailure(ex); }
    }
    void RenderPage()
    {
        if (!restoringScroll && pageScroll != null && renderedPage != null)
        {
            scrollPositions[renderedPage] = pageScroll.ScrollY;
            if (renderedPage == "Keys")
            {
                var first = keyRows.FirstOrDefault(k => k.Value.Bottom > pageScroll.ScrollY);
                visibleKey = first.Value != null ? first.Key : null;
                visibleKeyOffset = first.Value != null ? first.Value.Top - pageScroll.ScrollY : 0;
            }
        }
        restoringScroll = true;
        keyRows.Clear();
        startupStage = "display mode";
        dark = DisplayMode == "Dark" || (DisplayMode == "System" && (Resources.Configuration.UiMode & Android.Content.Res.UiMode.NightMask) == Android.Content.Res.UiMode.NightYes);
        SetTheme(dark ? Android.Resource.Style.ThemeMaterialNoActionBar : Android.Resource.Style.ThemeMaterialLightNoActionBar);
        var root = Column(); root.SetBackgroundColor(dark ? Color.Rgb(18,24,32) : Color.Rgb(245,248,251));
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R) root.SetOnApplyWindowInsetsListener(new SafeInsets(Dp(16), Dp(8)));
        startupStage = "header";
        var brand = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        brand.SetGravity(GravityFlags.CenterVertical);
        if (headerLogo == null)
        {
            using var options = new BitmapFactory.Options { InSampleSize = 4 };
            headerLogo = BitmapFactory.DecodeResource(Resources, Resources.GetIdentifier("squirrel_radio", "drawable", PackageName), options);
        }
        var logo = new ImageView(this); logo.SetImageBitmap(headerLogo); logo.SetScaleType(ImageView.ScaleType.FitCenter);
        logo.ContentDescription = "Squirrel holding a radio and making a shh gesture"; brand.AddView(logo, new LinearLayout.LayoutParams(Dp(44), Dp(44)));
        var brandTitle = Text(brand, "KFD Mobile", 25); brandTitle.SetTypeface(null, TypefaceStyle.Bold);
        brandTitle.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1);
        var settings = Button(brand, "⚙", () => { page = "Display"; Render(); }); settings.ContentDescription = "Settings and system appearance";
        settings.LayoutParameters = new LinearLayout.LayoutParams(Dp(48), Dp(48)); root.AddView(brand);
        Text(root, PackageManager.GetPackageInfo(PackageName, (PackageInfoFlags)0).VersionName, 12); Text(root, status, 14);
        startupStage = "navigation";
        foreach (var row in new[] { new[] { "Container", "Keys", "Groups" }, new[] { "USB", "MR", "RADIO" } })
        {
            var tabs = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            foreach (string name in row)
            {
                string label = name switch { "MR" => "Emulate", "RADIO" => "Radio", _ => name };
                var tab = Button(tabs, label, () => { page = name; Render(); });
                tab.LayoutParameters = new LinearLayout.LayoutParams(0, Dp(48), 1); tab.TextSize = 12;
                if (name == page) tab.SetTypeface(null, TypefaceStyle.Bold);
            }
            root.AddView(tabs);
        }
        var scroll = new ScrollView(this); pageScroll = scroll; renderedPage = page;
        content = Column(); scroll.AddView(content); root.AddView(scroll, new LinearLayout.LayoutParams(-1,0,1)); SetContentView(root);
        startupStage = "page " + page;
        switch(page) { case "Container": RenderContainer(); break; case "Keys": RenderKeys(); break; case "Groups": RenderGroups(); break; case "MR": RenderMr(); break; case "Display": RenderDisplay(); break; case "RADIO": RenderRadio(); break; case "Firmware": RenderFirmware(); break; default: RenderUsb(); break; }
        int y = scrollPositions.GetValueOrDefault(page);
        string restoredPage = page;
        scroll.Post(() =>
        {
            if (!ReferenceEquals(pageScroll, scroll)) return;
            if (restoredPage == "Keys" && visibleKey.HasValue && keyRows.TryGetValue(visibleKey.Value, out var row)) y = row.Top - visibleKeyOffset;
            scroll.ScrollTo(0, Math.Max(0, y));
            restoringScroll = false;
        });
    }
    public override void OnConfigurationChanged(Android.Content.Res.Configuration configuration)
    {
        base.OnConfigurationChanged(configuration);
        if (openDialogs.Count == 0) Render();
    }
    void RenderContainer()
    {
        Text(content, session.IsOpen ? $"Unlocked · {session.Inner.Keys.Count} keys · {session.Inner.Groups.Count} groups" : "No container open", 21);
        if (session.Dirty) Text(content, "Unsaved changes — save the container to keep them.");
        Button(content, "Create encrypted container", () => Discard(() => Password("Create container", true, password => Work("Creating container", () => { session.Create(password); selected.Clear(); }, () => { containerUri = null; remotePath = remoteRevision = null; saveLocationCanceled=false; containerName="keys.ekc"; revision++; ResetListPosition(); Render(); SaveContainer(saveAs: true); }))));
        Button(content, "Open .ekc container", () => Discard(OpenContainerDocument));
        Button(content, "Save container", () => SaveContainer(), session.IsOpen);
        Button(content, "Save as / export .ekc", () => SaveContainer(saveAs: true), session.IsOpen);
        Text(content, Remote ? "Autosave is always on. Remote saves include a timestamped backup. A failed remote save automatically creates a local encrypted recovery copy." : "Autosave is always on. Each save also creates an encrypted, timestamped backup in Downloads/KFDmobile/Backups.", 14);
        Text(content, storageLoadError ?? (Remote ? "Storage: " + storage.Kind + (storage.AlsoSaveLocally ? " + local copies" : " + local recovery on failure") : "Storage: Local / Android file picker"), 14);
        Button(content, "Storage settings", EditStorage);
        Button(content, "Local recovery copies", OpenRecoveryCopies);
        Text(content, !HasContainerDestination ? "Use Save as once for a new container. Autosave then updates that file." : "Autosave updates your container after edits and device operations. Save failures leave changes unsaved.", 14);
        Button(content, "Export .dkf to device", () =>
        {
            RequireStorage(); if (RemoteOnly) throw new InvalidOperationException("Local export is disabled in Storage settings.");
            int count = session.Inner.Keys.Count(ContainerSession.DkfSupported);
            Confirm("Export DKF?", $"Export {count} AES-256 / DES-OFB TEK(s). Other algorithms and KEKs are excluded. Group memberships are not included.\n\nChoose a password for the DKF, then a folder on this device. DKF export does not save changes to your .ekc container.", () =>
                Password("DKF export password", true, password => Work("Creating DKF", () => pendingDkf = session.ExportDkf(password), () =>
                {
                    pickerActive = true;
                    var intent = new Intent(Intent.ActionCreateDocument); intent.AddCategory(Intent.CategoryOpenable); intent.SetType("application/octet-stream");
                    intent.PutExtra(Intent.ExtraTitle, "keys.dkf"); intent.PutExtra(Intent.ExtraLocalOnly, true); StartActivityForResult(intent, 104);
                })));
        }, session.IsOpen);
        Button(content, "Change container password", () => Password("Change password", true, password => Work("Changing password", () => session.ChangePassword(password), Changed)), session.IsOpen);
        Button(content, "Lock / close container", () => Discard(Lock), session.IsOpen);
        Text(content, "Files use the KFDtool desktop .ekc format. Choose a folder when exporting. The app keeps no plaintext container on disk. Close the container when finished.", 14);

    }
    void RenderKeys()
    {
        if (!session.IsOpen) { Text(content, "Create or open a container first."); return; }
        Button(content, "Add key", () => EditKey(null));
        loadSelectedButton = Button(content, $"Load selected keys ({selected.Count})", LoadSelected, selected.Count > 0);
        Button(content, "Select all / clear selection", () => { if (selected.Count == session.Inner.Keys.Count) selected.Clear(); else foreach (var key in session.Inner.Keys) selected.Add(key.Id); Render(); });
        var hex = Check(content, "Show KID in hexadecimal", kidHex);
        hex.CheckedChange += (_, e) => {
            kidHex = e.IsChecked; GetSharedPreferences("keys", FileCreationMode.Private).Edit().PutBoolean("hex", kidHex).Apply(); Render();
        };
        string[] sortNames = { "KID: low to high", "KID: high to low", "Name: A to Z", "Name: Z to A", "CKR: low to high", "CKR: high to low", "Algorithm: A to Z", "Algorithm: Z to A" };
        Button(content, "Sort keys: " + sortNames[Math.Clamp(keySort, 0, sortNames.Length-1)], () =>
            ShowDialog(new AlertDialog.Builder(this).SetTitle("Sort keys").SetSingleChoiceItems(sortNames, keySort, (_, e) => {
                keySort=e.Which; GetSharedPreferences("keys",FileCreationMode.Private).Edit().PutInt("sort",keySort).Apply();
            }).SetPositiveButton("Done",(_,_)=>Render()).Create()));
        foreach (var key in KeyPresentation.Sort(session.Inner.Keys, keySort))
        {
            var card = new LinearLayout(this) { Orientation = Orientation.Vertical }; content.AddView(card);
            var title = Text(card, key.Name, 21); title.SetTypeface(null, TypefaceStyle.Bold); title.SetPadding(0,Dp(4),0,0);
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical); card.AddView(row); keyRows[key.Id] = card;
            var check = Check(row, $"KID {KeyPresentation.Kid(key.KeyId, kidHex)} · SLN {key.Sln} · ALG {KeyPresentation.Algorithm(key.AlgorithmId)}", selected.Contains(key.Id));
            check.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1);
            check.SetPadding(Dp(4), 0, Dp(4), Dp(4));
            check.CheckedChange += (_, e) => {
                if (e.IsChecked) selected.Add(key.Id); else selected.Remove(key.Id);
                loadSelectedButton.Text = $"Load selected keys ({selected.Count})"; loadSelectedButton.Enabled = !busy && selected.Count > 0;
            };
            var more = new ImageButton(this);
            more.SetImageResource(Android.Resource.Drawable.IcMenuMore);
            more.ContentDescription = "Actions for " + key.Name;
            more.Enabled = !busy;
            row.AddView(more, new LinearLayout.LayoutParams(Dp(48), Dp(48)));
            more.Click += (_, _) => {
                var menu = new PopupMenu(this, more); menu.Menu.Add("Edit key");
                menu.Menu.Add("Delete key");
                menu.MenuItemClick += (_, e) => { if (e.Item.TitleFormatted.ToString() == "Delete key") DeleteKey(key); else EditKey(key); }; menu.Show();
            };
            var divider = new View(this); divider.SetBackgroundColor(dark ? Color.Rgb(60,70,80) : Color.Rgb(210,216,222));
            content.AddView(divider, new LinearLayout.LayoutParams(-1, Dp(1)));
        }
        if (session.Inner.Keys.Count > 0) Button(content, "Go to top ↑", () => pageScroll.SmoothScrollTo(0, 0));
    }
    void DeleteKey(KeyItem key) => Confirm("Delete key?", $"Remove '{key.Name}' and its group memberships from this container?", () => {
        session.Inner.Keys.Remove(key); foreach (var group in session.Inner.Groups) group.Keys.Remove(key.Id);
        selected.Remove(key.Id); key.Key = ""; Changed(); Render();
    });

    void EditKey(KeyItem original)
    {
        var form = Column();
        var name = Field(form, "Name", original?.Name ?? "");
        bool editHex = kidHex;
        var kidLabel = Text(form, editHex ? "Key ID (hexadecimal)" : "Key ID (decimal)", 13);
        var kid = Field(form, "KID", KeyPresentation.Kid(original?.KeyId ?? 1, editHex), numeric: !editHex);
        var hexInput = Check(form, "Enter KID in hexadecimal", editHex);
        bool changingBase = false;
        hexInput.CheckedChange += (_, e) => {
            if (changingBase) return;
            try {
                int value = KeyPresentation.ParseKid(kid.Text, editHex);
                editHex = e.IsChecked; kid.InputType = editHex ? InputTypes.ClassText : InputTypes.ClassNumber;
                kid.Text = KeyPresentation.Kid(value, editHex); kidLabel.Text = editHex ? "Key ID (hexadecimal)" : "Key ID (decimal)";
            } catch (Exception ex) { changingBase = true; hexInput.Checked = editHex; changingBase = false; Error(ex); }
        };
        var sln = Field(form, "Storage location / SLN (decimal)", (original?.Sln ?? 1).ToString(), numeric: true);
        int[] algorithms = { 0x84, 0x81, 0x9f, 0xaa };
        int algIndex = Array.IndexOf(algorithms, original?.AlgorithmId ?? 0x84);
        var algorithm = Choices(form, "Algorithm", new[] { "AES-256 · 32 bytes", "DES-OFB · 8 bytes", "DES-XL · 8 bytes", "ARC4 · 5 bytes" }, Math.Max(0, algIndex));
        if (original != null && algIndex < 0) Text(form, "This key uses an unsupported algorithm. Cancel to preserve it unchanged.");
        var secret = Field(form, "Key (hexadecimal)", original?.Key ?? "", secret: true);
        var keyLengthStatus = Text(form, "", 14);
        int entryAlgorithm = algorithms[Math.Max(0, algIndex)];
        void UpdateKeyLength() {
            int max = KeyEntry.HexLength(entryAlgorithm), count = secret.Text.Length;
            keyLengthStatus.Text = count == max ? $"Slot filled · {count}/{max} hexadecimal digits" : $"{count}/{max} hexadecimal digits";
        }
        secret.SetFilters(new IInputFilter[] { new KeyInputFilter(() => entryAlgorithm,
            () => keyLengthStatus.Text = $"Maximum {KeyEntry.HexLength(entryAlgorithm)} hexadecimal digits (0-9, A-F). Entry rejected.") });
        secret.TextChanged += (_, _) => UpdateKeyLength();
        algorithm.ItemSelected += (_, e) => {
            int next = algorithms[e.Position];
            if (next == entryAlgorithm) return;
            entryAlgorithm = next; secret.Text = ""; UpdateKeyLength();
        };
        Text(form, "Changing the algorithm clears the entered key. Overlong pastes are rejected, not shortened.", 13);
        UpdateKeyLength();
        var show = Check(form, "Show key", false);
        var expand = Check(form, "Expand key editor", false);
        void UpdateKeyEditor() {
            int cursor = Math.Max(0, secret.SelectionStart);
            secret.InputType = InputTypes.ClassText | InputTypes.TextVariationVisiblePassword | InputTypes.TextFlagNoSuggestions | (expand.Checked ? InputTypes.TextFlagMultiLine : 0);
            secret.SetSingleLine(!expand.Checked); secret.SetMinLines(expand.Checked ? 3 : 1); secret.SetMaxLines(expand.Checked ? int.MaxValue : 1);
            secret.SetHorizontallyScrolling(!expand.Checked); secret.ImeOptions = Android.Views.InputMethods.ImeAction.Next | (Android.Views.InputMethods.ImeAction)Android.Views.InputMethods.ImeFlags.NoExtractUi;
            secret.TransformationMethod = show.Checked ? null : Android.Text.Method.PasswordTransformationMethod.Instance;
            secret.SetTypeface(Typeface.Monospace, TypefaceStyle.Normal); secret.SetSelection(Math.Min(cursor,secret.Text.Length));
        }
        show.CheckedChange += (_,_)=>UpdateKeyEditor(); expand.CheckedChange += (_,_)=>UpdateKeyEditor(); UpdateKeyEditor();
        Button(form, "Generate random key", () =>
        {
            int alg = algorithms[algorithm.SelectedItemPosition]; int length = alg == 0x84 ? 32 : alg == 0xaa ? 5 : 8;
            byte[] generated = RandomNumberGenerator.GetBytes(length);
            if (length == 8) for (int i = 0; i < length; i++) { int b = generated[i] & 0xfe; int ones = System.Numerics.BitOperations.PopCount((uint)b); generated[i] = (byte)(b | ((ones & 1) == 0 ? 1 : 0)); }
            secret.Text = Convert.ToHexString(generated); CryptographicOperations.ZeroMemory(generated);
        });
        var active = Check(form, "Use active keyset", original?.ActiveKeyset ?? true);
        var keyset = Field(form, "Keyset ID (decimal)", (original?.KeysetId ?? 1).ToString(), numeric: true);
        var type = Choices(form, "Key type", new[] { "Automatic (by SLN)", "TEK", "KEK" }, original?.KeyTypeKek == true ? 2 : original?.KeyTypeTek == true ? 1 : 0);
        var scroll = new ScrollView(this); scroll.AddView(form);
        var dialog = new AlertDialog.Builder(this).SetTitle(original == null ? "Add key" : "Edit key").SetView(scroll).SetNegativeButton("Cancel", (_, _) => { }).Create();
        Button(form, "Save key", () =>
        {
            var item = new KeyItem { Id = original?.Id ?? session.Inner.NextKeyNumber, Name = name.Text.Trim(), KeyId = KeyPresentation.ParseKid(kid.Text, editHex), Sln = int.Parse(sln.Text), AlgorithmId = algorithms[algorithm.SelectedItemPosition], Key = new string(secret.Text.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant(), ActiveKeyset = active.Checked, KeysetId = int.Parse(keyset.Text), KeyTypeAuto = type.SelectedItemPosition == 0, KeyTypeTek = type.SelectedItemPosition == 1, KeyTypeKek = type.SelectedItemPosition == 2 };
            ContainerSession.Validate(item);
            if (original == null) { session.Inner.Keys.Add(item); session.Inner.NextKeyNumber++; } else { int index = session.Inner.Keys.IndexOf(original); session.Inner.Keys[index] = item; original.Key = ""; }
            Changed(); secret.Text = ""; dialog.Dismiss(); Render();
        });
        if (original != null) Button(form, "Delete key", () => Confirm("Delete key?", "Remove this key and its group memberships from this container?", () => { session.Inner.Keys.Remove(original); foreach (var group in session.Inner.Groups) group.Keys.Remove(original.Id); selected.Remove(original.Id); original.Key = ""; Changed(); secret.Text = ""; dialog.Dismiss(); Render(); }));
        dialog.DismissEvent += (_, _) => secret.Text = "";
        ShowDialog(dialog);
        dialog.Window?.SetSoftInputMode(SoftInput.AdjustResize);
    }
    void RenderGroups()
    {
        if (!session.IsOpen) { Text(content, "Create or open a container first."); return; }
        Button(content, "Add group", () => EditGroup(null));
        foreach (var item in session.Inner.Groups)
        {
            var group = item; Text(content, $"{group.Name} · {group.Keys.Count} keys", 19);
            Button(content, "Load group keys", () => { selected.Clear(); foreach (int id in group.Keys) if (session.Inner.Keys.Any(k => k.Id == id)) selected.Add(id); LoadSelected(); });
            Button(content, "Edit group", () => EditGroup(group));
        }
    }
    void EditGroup(GroupItem original)
    {
        var form = Column(); var name = Field(form, "Group name", original?.Name ?? "");
        var members = new Dictionary<int, CheckBox>();
        foreach (var key in session.Inner.Keys) members.Add(key.Id, Check(form, key.Name, original?.Keys.Contains(key.Id) ?? false));
        var scroll = new ScrollView(this); scroll.AddView(form);
        var dialog = new AlertDialog.Builder(this).SetTitle("Key group").SetView(scroll).SetNegativeButton("Cancel", (_, _) => { }).Create();
        Button(form, "Save group", () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) throw new ArgumentException("Enter a group name.");
            var group = new GroupItem { Id = original?.Id ?? session.Inner.NextGroupNumber, Name = name.Text.Trim(), Keys = members.Where(x => x.Value.Checked).Select(x => x.Key).ToList() };
            if (original == null) { session.Inner.Groups.Add(group); session.Inner.NextGroupNumber++; } else session.Inner.Groups[session.Inner.Groups.IndexOf(original)] = group;
            Changed(); dialog.Dismiss(); Render();
        });
        if (original != null) Button(form, "Delete group", () => Confirm("Delete group?", "The keys themselves will remain in the container.", () => { session.Inner.Groups.Remove(original); Changed(); dialog.Dismiss(); Render(); }));
        ShowDialog(dialog);
    }
    void RenderUsb()
    {
        Text(content, "USB connection", 22);
        RenderDeviceMode();
        Button(content, "Refresh USB devices", Render);
        var devices = usb.DeviceList.Values.ToArray();
        if (devices.Length == 0) Text(content, "No USB devices found.");
        foreach (var device in devices)
        {
            var d = device;
            Text(content, $"{d.ProductName ?? "USB device"}\nVID:PID {d.VendorId:X4}:{d.ProductId:X4} · {UsbSerialTransport.Driver(d)}", 14);
            Button(content, usbName == d.DeviceName && usb.HasPermission(d) ? "Selected · USB permission granted" : "Select and allow USB", () => SelectUsb(d), !UsbSerialTransport.Driver(d).StartsWith("Unsupported"));
        }
        Button(content, "Read keyloader information", () => WithUsb(() => { string info=""; Work("Reading keyloader", () =>
        {
            var ap = new AdapterProtocol(usbName, shield ? TwiKfdDevice.KfdShield : TwiKfdDevice.KfdTool);
            try { ap.Open(); ap.Clear(); var version = ap.ReadAdapterProtocolVersion(); var firmware = ap.ReadFirmwareVersion(); var model = ap.ReadModelId(); info=$"Adapter protocol {version}\nFirmware {string.Join(".", firmware)}\nModel {model:X2}"; }
            finally { ap.Close(); }
        }, () => Message("Keyloader information", info)); }));
        Button(content, "Detect connected radio", () => WithUsb(() => { var device = Device(); Work("Detecting radio", () => InteractTwiKfdtool.CheckTargetMrConnection(device), () => Message("Radio detected", "The connected radio completed the connection check.")); }));

        Button(content, "Firmware updates", () => { page = "Firmware"; Render(); });
        Text(content, "Recent activity", 20);
        string[] recent; lock (events) recent = events.ToArray();
        ActivityList(content, recent);
    }
    void FirmwareProgress(string message) => RunOnUiThread(() => { firmwareStatus = message; if (firmwareOutput != null) firmwareOutput.Text = message; });
    void RenderFirmware()
    {
        Text(content, "Firmware updates", 22);
        Button(content, "OmahaCommSys firmware downloads (current / older)", () => StartActivity(new Intent(Intent.ActionView, Uri.Parse("https://github.com/omahacommsys/KFDtool/releases"))));
        Button(content, "OmahaCommSys firmware flashing instructions", () => StartActivity(new Intent(Intent.ActionView, Uri.Parse("https://github.com/omahacommsys/KFDtool#firmware-flashing"))));
        Text(content, "KFDtool KFD100 packages are checked against the connected model and hardware revision. Choose a current or older .cfp/.ufp package. KFDshield and KFDpico require their own firmware tools.", 14);
        Button(content, "Open firmware package", () =>
        {
            pickerActive = true;
            var intent = new Intent(Intent.ActionOpenDocument); intent.AddCategory(Intent.CategoryOpenable); intent.SetType("*/*");
            StartActivityForResult(intent, 103);
        });
        foreach (var update in firmwareUpdates)
        {
            var chosen = update;
            Text(content, $"Firmware {update.AppVersion} | RAM BSL {update.RamBslVersion}", 17);
            Text(content, string.Join("; ", update.ProductModel.Select(m => m.Name + " revisions " + string.Join(", ", m.Revision))), 13);
            Button(content, "Check device and prepare update", () => PrepareFirmware(chosen));
        }
        firmwareOutput = Text(content, firmwareStatus, 14);
    }
    void PrepareFirmware(KFDtool.Adapter.Bundle.Update update) => WithUsb(() =>
    {
        if (shield) throw new InvalidOperationException("The upstream MSP430 updater supports TWI (KFDtool), not KFDshield / other.");
        var plan = new FirmwarePlan(update); string port = usbName; string revision = "", installed = "", serial = "";
        Work("Checking firmware compatibility", () =>
        {
            var ap = new AdapterProtocol(port, TwiKfdDevice.KfdTool);
            try
            {
                ap.Open(); ap.Clear();
                if (ap.ReadModelId() != 1) throw new IOException("Only the KFD100 model supports this firmware update protocol.");
                revision = string.Join(".", ap.ReadHardwareRevision()); installed = string.Join(".", ap.ReadFirmwareVersion()); serial = Convert.ToHexString(ap.ReadSerialNumber());
                if (!plan.Supports("KFD100", revision)) throw new IOException("The firmware package does not list this hardware revision (" + revision + ").");
            }
            finally { ap.Close(); }
        }, () => Confirm("Install firmware?", $"KFD100 hardware {revision}\nInstalled: {installed}\nSelected: {update.AppVersion}\n\nThis erases and replaces adapter firmware. Keep the USB cable connected and the app open. Android may request USB access again when the adapter restarts. Use a firmware package from a trusted source.", () => RunFirmware(plan, port, serial, revision)));
    });
    void RunFirmware(FirmwarePlan plan, string port, string serial, string revision)
    {
        firmwareBusy = true;
        Work("Updating adapter firmware", () =>
        {
            try
            {
                var ap = new AdapterProtocol(port, TwiKfdDevice.KfdTool);
                try
                {
                    ap.Open(); ap.Clear();
                    if (ap.ReadModelId() != 1 || Convert.ToHexString(ap.ReadSerialNumber()) != serial || string.Join(".", ap.ReadHardwareRevision()) != revision) throw new IOException("The connected device changed. Prepare the update again.");
                    if (usb.DeviceList.Values.Any(UsbBslTransport.IsBootloader)) throw new IOException("Disconnect other MSP430 bootloader devices before continuing.");
                    FirmwareProgress("Entering bootloader; approve Android USB access when prompted."); ap.EnterBslMode();
                }
                finally { ap.Close(); }
                using (var loader = WaitForBootloader()) { FirmwareProgress("Unlocking adapter and loading RAM bootloader."); BslUpdate.LoadRam(loader, plan); }
                Thread.Sleep(1000);
                using (var loader = WaitForBootloader()) BslUpdate.Program(loader, plan, FirmwareProgress);
            }
            catch { FirmwareProgress("Update stopped. Firmware may be incomplete; keep the adapter available for bootloader recovery using desktop KFDtool."); throw; }
            finally { firmwareBusy = false; }
        });
    }
    UsbBslTransport WaitForBootloader()
    {
        var deadline = DateTime.UtcNow.AddSeconds(60); string requested = null;
        while (DateTime.UtcNow < deadline)
        {
            var devices = usb.DeviceList.Values.Where(UsbBslTransport.IsBootloader).ToArray();
            if (devices.Length > 1) throw new IOException("More than one MSP430 bootloader is connected.");
            if (devices.Length == 1)
            {
                var device = devices[0];
                if (usb.HasPermission(device)) return new UsbBslTransport(usb, device);
                if (requested != device.DeviceName)
                {
                    requested = device.DeviceName;
                    RunOnUiThread(() =>
                    {
                        var flags = PendingIntentFlags.UpdateCurrent;
                        if (Build.VERSION.SdkInt >= BuildVersionCodes.S) flags |= PendingIntentFlags.Mutable;
                        using var permission = PendingIntent.GetBroadcast(this, 1, new Intent(PermissionAction).SetPackage(PackageName), flags);
                        usb.RequestPermission(device, permission);
                    });
                }
            }
            Thread.Sleep(200);
        }
        throw new TimeoutException("Bootloader did not reconnect with USB permission within 60 seconds.");
    }
    void RenderDeviceMode()
    {
        Button(content, "Device type: " + (shield ? "TWI (KFDshield / other)" : "TWI (KFDtool)") + " ▾", () =>
            ShowDialog(new AlertDialog.Builder(this).SetTitle("Device type")
                .SetSingleChoiceItems(new[] { "TWI (KFDtool)", "TWI (KFDshield / other)" }, shield ? 1 : 0, (_, e) => {
                    shield = e.Which == 1; GetSharedPreferences("device", FileCreationMode.Private).Edit().PutBoolean("shield", shield).Apply();
                }).SetPositiveButton("Done", (_, _) => Render()).Create()));
        if (page != "MR") Text(content, "KFDpico uses TWI (KFDshield / other).", 13);
    }

    void RenderDisplay()
    {
        Text(content, "Container", 22).SetTypeface(null, TypefaceStyle.Bold);
        var closeOnLeave = Check(content, "Close container when leaving the app", CloseContainerOnLeave);
        closeOnLeave.CheckedChange += (_, args) => GetSharedPreferences("container", FileCreationMode.Private).Edit().PutBoolean("close_on_leave", args.IsChecked).Apply();
        Text(content, "On: save and close when you leave using Back, Home or another app. Off: keep the container open when you return. Opening a file picker does not close it. Phone locking still saves and closes it. If saving fails, unsaved changes are retained. Android may end the app; keeping it open does not survive a force-stop or restart.", 14);
        Button(content, "Storage settings", EditStorage);
        Text(content, "Appearance", 22).SetTypeface(null, TypefaceStyle.Bold);
        Text(content, "Choose a display mode. System follows your device's appearance setting.", 14);
        foreach (string mode in new[] { "System", "Light", "Dark" })
        {
            string choice = mode;
            Button(content, (DisplayMode == mode ? "Selected: " : "") + mode, () =>
            {
                GetSharedPreferences("appearance", FileCreationMode.Private).Edit().PutString("mode", choice).Apply(); Render();
            });
        }
        Text(content, "About", 22).SetTypeface(null, TypefaceStyle.Bold);
        Button(content, "Credits", ShowCredits);
        Button(content, "Third-party licenses", () => { using var reader = new StreamReader(Assets.Open("THIRD-PARTY-NOTICES.txt")); Message("Third-party licenses", reader.ReadToEnd()); });
    }

    void ShowCredits()
    {
        var body = Column();
        void Heading(string title) => Text(body, title, 20).SetTypeface(null, TypefaceStyle.Bold);
        void Link(string label, string url)
        {
            var link = Text(body, label, 16);
            link.SetTextColor(dark ? Color.Rgb(144, 202, 249) : Color.Rgb(21, 101, 192));
            link.PaintFlags |= PaintFlags.UnderlineText;
            link.SetMinimumHeight(Dp(48));
            link.Gravity = GravityFlags.CenterVertical;
            link.Focusable = true;
            link.ContentDescription = label + ", GitHub link";
            link.Click += (_, _) => {
                try { StartActivity(new Intent(Intent.ActionView, Uri.Parse(url))); }
                catch (ActivityNotFoundException) { Message("GitHub link", "No browser is available to open " + url); }
            };
        }
        Heading("KFDmobile");
        Link("AESFTW", "https://github.com/AESFTW");
        Link("ComSec0", "https://github.com/ComSec0");
        Link("View KFDmobile on GitHub ↗", "https://github.com/AESFTW/KFDmobile");
        Heading("Original KFDtool");
        Link("duggerd", "https://github.com/duggerd");
        Link("dB-SPL", "https://github.com/dB-SPL");
        Link("grover556", "https://github.com/grover556");
        Link("OmahaCommSys", "https://github.com/omahacommsys");
        Link("View KFDtool on GitHub ↗", "https://github.com/omahacommsys/KFDtool");
        var scroll = new ScrollView(this); scroll.AddView(body);
        ShowDialog(new AlertDialog.Builder(this).SetTitle("Credits").SetView(scroll)
            .SetPositiveButton("Close", (_, _) => { }).Create());
    }

    void RenderMr()
    {
        RenderDeviceMode();
        Button(content, "USB connection settings", () => { page = "USB"; Render(); });
        Text(content, "P25 MR emulator", 20);
        Text(content, "KVL: Load one algorithm at a time — ARC4, AES, or DES. Do not use Keyload-All; transfers containing multiple algorithms may time out.", 16).SetTypeface(null, TypefaceStyle.Bold);
        Text(content, "Emulates a receiving radio. Connect another keyloader to the adapter's radio interface and send keys from that loader. Received keys appear here. When local key exports are enabled, plaintext text files are also saved in Downloads. With local exports disabled, import keys into an open container to save them using your storage settings. If a container is open, you will be prompted to add each received key. Container changes save automatically after the device operation. Apply-to-all choices last for the current MR session; replacements have a separate choice.", 14);
        Button(content, "Start MR emulator", () => WithUsb(StartEmulator));
        var stop = Button(content, "Stop MR emulator", () => emulatorCancellation?.Cancel());
        stop.Enabled = emulatorCancellation != null;
        Button(content, "Clear received data", () => { mrStatus = ""; receivedKeys.Clear(); Render(); });
        mrOutput = Text(content, mrStatus, 14); mrOutput.SaveEnabled = false;
        mrCards = Column(); content.AddView(mrCards); RenderReceivedKeys();
        mrSaveOutput = Text(content, mrSaveStatus, 14);
    }

    void PromptNextImport()
    {
        if (importDialog != null || backgrounded || IsDestroyed || IsFinishing) return;
        while (importQueue.Count > 0)
        {
            var pending = importQueue.Dequeue();
            if (!session.IsOpen || !ReferenceEquals(session.Inner, pending.Container)) continue;
            var key = pending.Key;
            var matches = session.Inner.Keys.Where(k => k.Sln == key.Sln && k.KeyId == key.KeyId && k.AlgorithmId == key.AlgorithmId && k.KeysetId == key.KeysetId).ToArray();
            if (matches.Length > 1) { AddEvent("MR import skipped: multiple matching container entries. Resolve duplicates first."); continue; }
            var match = matches.SingleOrDefault();
            bool replacement = match != null;
            void Import()
            {
                if (!session.IsOpen || !ReferenceEquals(session.Inner, pending.Container)) return;
                session.ImportReceived(key, match?.Id); Changed();
                status = "MR key added to container";
            }
            var choice = importPolicy.Choice(replacement);
            if (choice.HasValue)
            {
                if (choice.Value) { try { Import(); } catch (Exception ex) { Error(ex); } }
                continue;
            }
            var form = Column();
            Text(form, $"SLN: {key.Sln}\nKey ID: {KeyPresentation.Kid(key.KeyId, kidHex)}\nKeyset: {key.KeysetId}\nAlgorithm: {KeyPresentation.Algorithm(key.AlgorithmId)}\n\n" +
                (match == null ? "Add this received key to the open container?" : $"Replace key data in '{match.Name}'? Its name and group memberships will be kept."));
            var all = Check(form, replacement ? "Apply to all replacements in this MR session" : "Apply to all new keys in this MR session", false);
            all.Enabled = true;
            var dialog = new AlertDialog.Builder(this).SetTitle("Save MR key to open container?").SetView(form)
                .SetNegativeButton("Skip", (_, _) => { if (all.Checked) importPolicy.Apply(replacement, false); })
                .SetPositiveButton(match == null ? "Add key" : "Replace key", (_, _) =>
                {
                    try { Import(); if (all.Checked) importPolicy.Apply(replacement, true); Render(); }
                    catch (Exception ex) { Error(ex); }
                }).Create();
            importDialog = dialog;
            dialog.DismissEvent += (_, _) => { importDialog = null; PromptNextImport(); };
            ShowDialog(dialog); return;
        }
    }

    async void StartEmulator()
    {
        if (busy) return;
        if (Build.VERSION.SdkInt < BuildVersionCodes.Q && CheckSelfPermission(Android.Manifest.Permission.WriteExternalStorage) != Permission.Granted)
        {
            status = "Allow storage access, then tap Start MR emulator again."; Render();
            RequestPermissions(new[] { Android.Manifest.Permission.WriteExternalStorage }, 201); return;
        }
        importPolicy.Reset();
        var exports = new List<Task>();
        var receivingContainer = session.Inner;
        mrSaveStatus = RemoteOnly ? "Local key exports disabled: import received keys into an open container to save them." : "Received keys will be saved to Downloads.";
        string port = usbName; var model = shield ? TwiKfdDevice.KfdShield : TwiKfdDevice.KfdTool;
        var cancellation = new CancellationTokenSource(); emulatorCancellation = cancellation;
        busy = true; mrStatus = "Preparing USB adapter. Wait for Ready before sending from the KVL."; status = "Preparing MR emulator";
        Window.AddFlags(WindowManagerFlags.KeepScreenOn); Render();
        try
        {
            await Task.Run(() =>
            {
                var ap = new AdapterProtocol(port, model);
                try
                {
                    ap.Open(); ap.Clear();
                    using var registration = cancellation.Token.Register(ap.Cancel);
                    cancellation.Token.ThrowIfCancellationRequested();
                    RunOnUiThread(() =>
                    {
                        if (cancellation.IsCancellationRequested || IsDestroyed) return;
                        mrStatus = "Ready -- send keys from the KVL now.";
                        status = "MR emulator listening - press Stop to finish";
                        if (mrOutput != null) mrOutput.Text = mrStatus;
                    });
                    var emulator = new KFDtool.P25.ThreeWire.ThreeWireProtocol(ap);
                    emulator.KeyReceived += key =>
                    {
                        RunOnUiThread(() => { if (IsDestroyed || backgrounded) return; receivedKeys.Add((DateTime.Now, key)); if(receivedKeys.Count>100)receivedKeys.RemoveAt(0); RenderReceivedKeys(); });
                        if (receivingContainer != null) RunOnUiThread(() =>
                        {
                            if (backgrounded || IsDestroyed || !ReferenceEquals(session.Inner, receivingContainer)) return;
                            importQueue.Enqueue((key, receivingContainer)); PromptNextImport();
                        });
                        // File I/O runs separately so it cannot delay the radio acknowledgment.
                        if (!RemoteOnly) exports.Add(Task.Run(() =>
                        {
                            string result;
                            try { result = "Saved to Downloads: " + MrKeyExport.Save(ApplicationContext, key); }
                            catch { result = $"Save failed for SLN {key.Sln}, Key ID {key.KeyId}. Check storage access and free space; resend to retry."; }
                            RunOnUiThread(() =>
                            {
                                if (IsDestroyed) return;
                                mrSaveStatus = result; AddEvent(result);
                                if (mrSaveOutput != null) mrSaveOutput.Text = result;
                            });
                        }));
                    };
                    emulator.StatusChanged += (_, _) =>
                    {
                        string received = emulator.Status;
                        RunOnUiThread(() =>
                        {
                            if (cancellation.IsCancellationRequested || IsDestroyed) return;
                            mrStatus = received;
                            if (mrOutput != null) mrOutput.Text = mrStatus;
                        });
                    };
                    emulator.MrRunProducer();
                }
                finally { ap.Close(); }
            });
            status = "MR emulator stopped";
        }
        catch (System.OperationCanceledException) { status = "MR emulator stopped"; }
        catch (Exception ex) { Error(ex); }
        finally
        {
            await Task.WhenAll(exports);
            emulatorCancellation = null; cancellation.Dispose(); busy = false;
            Window.ClearFlags(WindowManagerFlags.KeepScreenOn);
            if (!IsFinishing && !IsDestroyed) Render();
        }
    }

    void ShowStartupFailure(Exception ex)
    {
        startupFailed = true;
        string details = $"KFD Mobile 0.1.9 startup diagnostic\nDevice: {Build.Manufacturer} {Build.Model}\nAndroid: {Build.VERSION.Release} / API {(int)Build.VERSION.SdkInt}\nStage: {startupStage}\n";
        for (Exception cause = ex; cause != null; cause = cause.InnerException)
            details += $"\n{cause.GetType().FullName}\n{cause.StackTrace}\n";
        // No container contents, key data or exception payloads are collected.
        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        layout.SetPadding(Dp(20), Dp(48), Dp(20), Dp(24)); layout.SetBackgroundColor(Color.White);
        var title = new TextView(this) { Text = "KFD Mobile could not open", TextSize = 22 }; title.SetTextColor(Color.Black); layout.AddView(title);
        var copy = new Button(this) { Text = "Copy startup diagnostic" };
        copy.Click += (_, _) => { ((Android.Content.ClipboardManager)GetSystemService(ClipboardService)).PrimaryClip = ClipData.NewPlainText("KFD Mobile startup diagnostic", details); copy.Text = "Copied - paste into this support conversation"; };
        layout.AddView(copy);
        var report = new TextView(this) { Text = details, TextSize = 13 }; report.SetTextColor(Color.Black); report.SetTextIsSelectable(true);
        var scroll = new ScrollView(this); scroll.AddView(report); layout.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        SetContentView(layout);
    }
    void SelectUsb(UsbDevice device)
    {
        usbName = device.DeviceName;
        if (usb.HasPermission(device)) { status = "USB device selected"; Render(); return; }
        var flags = PendingIntentFlags.UpdateCurrent;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.S) flags |= PendingIntentFlags.Mutable;
        var intent = new Intent(PermissionAction).SetPackage(PackageName);
        using var permission = PendingIntent.GetBroadcast(this, 0, intent, flags);
        usb.RequestPermission(device, permission);
    }
    void WithUsb(Action action)
    {
        if (usbName == null || !usb.DeviceList.TryGetValue(usbName, out var d) || !usb.HasPermission(d)) { page = "USB"; status = "Select a USB keyloader and grant permission first."; Render(); Message("Select KFD first", "Select your KFD on the USB tab and grant USB permission before starting this operation."); return; }
        action();
    }
    BaseDevice Device() => new() { DeviceType = BaseDevice.DeviceTypeOptions.TwiKfdDevice, KfdDeviceType = shield ? TwiKfdDevice.KfdShield : TwiKfdDevice.KfdTool, TwiKfdtoolDevice = new() { ComPort = usbName } };
    void LoadSelected() => WithUsb(() =>
    {
        var keys = session.Inner.Keys.Where(k => selected.Contains(k.Id)).ToList();
        foreach (var key in keys) ContainerSession.Validate(key);
        if (keys.Count == 0) return;
        var device = Device();
        ConfirmKeyLoad(keys, () => Work($"0 of {keys.Count} keys loaded · {keys.Count} remaining — keep USB connected", null,
            () => Message("Keyload successful", $"Loaded {keys.Count} key(s) into the connected radio."), reportingOperation: report =>
        {
            var commands = keys.Select(ContainerSession.ToCommand).ToList();
            try { InteractTwiKfdtool.Keyload(device, commands, progress: (loaded, total) => report($"{loaded} of {total} keys loaded · {total - loaded} remaining — keep USB connected")); } finally { foreach (var command in commands) { for (int i = 0; i < command.Key.Count; i++) command.Key[i] = 0; command.Key.Clear(); } }
        }));
    });
    void LaunchDocument(bool save)
    {
        pickerActive = true;
        var intent = new Intent(save ? Intent.ActionCreateDocument : Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable); intent.SetType(save ? "application/octet-stream" : "*/*");
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantPersistableUriPermission);
        if (save) intent.PutExtra(Intent.ExtraTitle, containerName);
        try { StartActivityForResult(intent, save ? 102 : 101); } catch { pickerActive = false; throw; }
    }
    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode,permissions,grantResults);
        if(requestCode==202 && grantResults.Length>0 && grantResults[0]==Permission.Granted)ScheduleSave();
    }
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
    {
        base.OnActivityResult(requestCode, resultCode, data); pickerActive = false;
        if (resultCode != Result.Ok || data?.Data == null) { if(requestCode==102)saveLocationCanceled=true; pendingExport = null; pendingDkf = null; afterSave = null; return; }
        Uri uri = data.Data;
        if(requestCode==105) {
            var accept=importedSshKey; importedSshKey=null;
            Work("Reading SSH key",()=> { using var stream=ContentResolver.OpenInputStream(uri); using var memory=new MemoryStream(); var buffer=new byte[4096]; int count;
                while((count=stream.Read(buffer,0,buffer.Length))>0){if(memory.Length+count>65536)throw new IOException("SSH key exceeds 64 KB.");memory.Write(buffer,0,count);}
                string key=System.Text.Encoding.UTF8.GetString(memory.ToArray());RunOnUiThread(()=>accept?.Invoke(key)); }); return;
        }
        if(requestCode==102)saveLocationCanceled=false;
        if (requestCode == 101 || requestCode == 102)
        {
            try { ContentResolver.TakePersistableUriPermission(uri, data.Flags & (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission)); }
            catch (Exception) { /* Some providers grant access only for this activity session. */ }
        }
        if (requestCode == 104 && pendingDkf != null)
        {
            Work("Saving DKF", () => { using var output = ContentResolver.OpenOutputStream(uri, "wt"); output.Write(pendingDkf); output.Flush(); pendingDkf = null; }); return;
        }
        if (requestCode == 103)
        {
            Work("Reading firmware package", () =>
            {
                using var input = ContentResolver.OpenInputStream(uri); using var memory = new MemoryStream(); var buffer = new byte[8192]; int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0) { memory.Write(buffer, 0, n); if (memory.Length > 8 * 1024 * 1024) throw new IOException("Firmware package exceeds 8 MB."); }
                firmwareUpdates = FirmwarePlan.ReadPackage(memory.ToArray());
                firmwareStatus = $"Loaded {firmwareUpdates.Count} update(s); package checksum verified.";
            }); return;
        }
        if (requestCode == 101) Password("Unlock container", false, password => Work("Opening container", () =>
        {
            using var input = ContentResolver.OpenInputStream(uri); using var memory = new MemoryStream(); var block = new byte[8192]; int n;
            while ((n = input.Read(block, 0, block.Length)) > 0) { memory.Write(block, 0, n); if (memory.Length > 8 * 1024 * 1024) throw new IOException("Container exceeds the 8 MB file limit."); }
            session.Open(memory.ToArray(), password); selected.Clear();
        }, () => { containerUri = uri; remotePath = remoteRevision = null; containerName=DocumentName(uri); revision++; ResetListPosition(); Render(); }));
        else if (requestCode == 102 && pendingExport != null) WriteContainer(uri);
    }
    void Password(string title, bool confirm, Action<string> use)
    {
        var form = Column(); var password = Field(form, "Password", secret: true); EditText repeat = confirm ? Field(form, "Confirm password", secret: true) : null;
        var dialog = new AlertDialog.Builder(this).SetTitle(title).SetView(form).SetNegativeButton("Cancel", (_, _) => { }).Create();
        Button(form, "Continue", () => { if (string.IsNullOrEmpty(password.Text)) throw new ArgumentException("Enter a password."); if (repeat != null && password.Text != repeat.Text) throw new ArgumentException("Passwords do not match."); string value = password.Text; password.Text = ""; if (repeat != null) repeat.Text = ""; dialog.Dismiss(); use(value); });
        dialog.DismissEvent += (_, _) => { password.Text = ""; if (repeat != null) repeat.Text = ""; };
        ShowDialog(dialog);
    }
    void Message(string title, string message) => ShowDialog(new AlertDialog.Builder(this).SetTitle(title).SetMessage(message).SetPositiveButton("OK", (_, _) => { }).Create());
    void Confirm(string title, string message, Action yes) => ShowDialog(new AlertDialog.Builder(this).SetTitle(title).SetMessage(message).SetNegativeButton("Cancel", (_, _) => { }).SetPositiveButton("Continue", (_, _) => { try { yes(); } catch (Exception ex) { Error(ex); } }).Create());
    void ResetListPosition() { pageScroll = null; scrollPositions.Clear(); visibleKey = null; }
    void Changed()
    {
        revision++; session.Dirty = true; ScheduleSave();
    }
    async void ScheduleSave()
    {
        autoSaveDelay?.Cancel();
        if (!session.IsOpen || !session.Dirty || (!HasContainerDestination && saveLocationCanceled)) return;
        var delay = autoSaveDelay = new CancellationTokenSource();
        try
        {
            await Task.Delay(800, delay.Token);
            while (busy || pickerActive || openDialogs.Count != 0) await Task.Delay(300, delay.Token);
            delay.Token.ThrowIfCancellationRequested();
            if (session.IsOpen && session.Dirty && !IsDestroyed && (HasContainerDestination || !backgrounded)) SaveContainer();
        }
        catch (System.OperationCanceledException) { }
    }
    void SaveContainer(Action continuation = null, bool saveAs = false)
    {
        if (busy || !session.IsOpen) return;
        if(!Remote && Build.VERSION.SdkInt<BuildVersionCodes.Q && CheckSelfPermission(Android.Manifest.Permission.WriteExternalStorage)!=Permission.Granted)
        {
            status="Allow storage access for encrypted backups, then save again.";
            if(!backgrounded)RequestPermissions(new[]{Android.Manifest.Permission.WriteExternalStorage},202);
            return;
        }
        autoSaveDelay?.Cancel();
        try
        {
            RequireStorage();
            // Capture on the UI thread so edits cannot race encryption or its revision.
            pendingExport = session.Save(); pendingStamp = new SaveStamp(session.Inner, revision);
            afterSave = continuation;
            if (Remote) { if (saveAs || remotePath == null) ChooseRemoteSave(); else WriteRemoteContainer(remotePath, remoteRevision); }
            else if (saveAs || containerUri == null) LaunchDocument(true); else WriteContainer(containerUri);
        }
        catch (Exception ex) { pendingExport = null; afterSave = null; Error(ex); }
    }
    void WriteContainer(Uri uri)
    {
        var bytes = pendingExport; var stamp = pendingStamp; var continuation = afterSave;
        pendingExport = null; afterSave = null;
        string savedName=DocumentName(uri), backupError=null, backupName=null;
        Work("Saving encrypted container", () =>
        {
            try { backupName=ContainerBackup.Save(ApplicationContext,BackupNaming.Name(savedName,DateTimeOffset.Now),bytes); AddEvent("Encrypted backup saved: " + backupName); }
            catch(Exception ex) { backupError=ex.Message; }
            using var output = ContentResolver.OpenOutputStream(uri, "wt") ?? throw new IOException("Cannot write this file. Use Save as to choose a writable location.");
            output.Write(bytes, 0, bytes.Length); output.Flush();
        }, () =>
        {
            if (stamp.Matches(session.Inner, revision))
            {
                containerUri = uri; containerName=savedName;
                if(backupError!=null){session.Dirty=true;Error(new IOException("Original container saved, but encrypted backup failed. Changes retained for retry. " + backupError));return;}
                session.Dirty = false;status=$"Saved {savedName} · {DateTime.Now:yyyy-MM-dd HH:mm:ss}";AddEvent(status);Render();continuation?.Invoke();
            }
            else { status = "Saved an earlier revision; newer changes are still unsaved."; Render(); ScheduleSave(); }
        });
    }
    string DocumentName(Uri uri)
    {
        try { using var cursor=ContentResolver.Query(uri,new[]{"_display_name"},null,null,null);if(cursor!=null && cursor.MoveToFirst() && !cursor.IsNull(0))return cursor.GetString(0); }
        catch { }
        return containerName;
    }
    void Discard(Action action)
    {
        autoSaveDelay?.Cancel();
        if (!session.Dirty) { action(); return; }
        SaveContainer(action);
    }
    void ShowDialog(Dialog dialog) { openDialogs.Add(dialog); dialog.DismissEvent += (_, _) => openDialogs.Remove(dialog); dialog.Show(); dialog.Window?.AddFlags(WindowManagerFlags.Secure); }
    void Lock() { autoSaveDelay?.Cancel(); containerUri = null; remotePath = remoteRevision = null; revision++; ResetListPosition(); importPolicy.Reset(); importQueue.Clear(); importDialog?.Dismiss(); mrStatus = ""; foreach (var dialog in openDialogs.ToArray()) dialog.Dismiss(); session.Dispose(); selected.Clear(); status = "Container locked"; Render(); }
    async void Work(string message, Action operation, Action success = null, Action<Exception> failure = null, Action<Action<string>> reportingOperation = null)
    {
        if (busy) return;
        busy = true; status = message; Window.AddFlags(WindowManagerFlags.KeepScreenOn); Render();
        var progressBody = Column(); progressBody.AddView(new ProgressBar(this) { Indeterminate = true }); var progressText = Text(progressBody, message);
        var progress = new AlertDialog.Builder(this).SetTitle("Please wait").SetView(progressBody).SetCancelable(false).Create(); ShowDialog(progress);
        bool ok = false; Exception failed = null; string latestMessage = message;
        try { await Task.Run(() => { if (reportingOperation != null) reportingOperation(text => { latestMessage = text; RunOnUiThread(() => { if (progress.IsShowing) progressText.Text = text; }); }); else operation(); }); status = latestMessage + " — completed"; AddEvent(status); ok = true; }
        catch (Exception ex) { failed = ex; }
        finally { progress.Dismiss(); busy = false; Window.ClearFlags(WindowManagerFlags.KeepScreenOn); if (!IsFinishing && !IsDestroyed) Render(); }
        try { if (ok) success?.Invoke(); else if (failure != null) failure(failed); else if (failed != null) Error(failed); }
        catch (Exception ex) { Error(ex); }
    }
    void AddEvent(string message) { lock (events) { events.Add($"{DateTime.Now:HH:mm:ss} {message}"); if (events.Count > 40) events.RemoveAt(0); } }
    void Error(Exception ex)
    {
        if (ex is StorageConflictException && session.IsOpen && !IsFinishing && !IsDestroyed)
        {
            status = "Remote conflict — unsaved edits retained"; AddEvent(status);
            ShowDialog(new AlertDialog.Builder(this).SetTitle("Remote file changed").SetMessage(ex.Message)
                .SetNegativeButton("Keep editing", (_, _) => { }).SetPositiveButton("Save a copy", (_, _) => SaveContainer(saveAs: true)).Create()); return;
        }
        string message = SafeStorageError(ex);
        if (session.IsOpen) foreach (var key in session.Inner.Keys) if (!string.IsNullOrEmpty(key.Key)) message = message.Replace(key.Key, "[redacted]", StringComparison.OrdinalIgnoreCase);
        status = "Operation failed: " + message; AddEvent(status);
        if (!IsFinishing && !IsDestroyed) ShowDialog(new AlertDialog.Builder(this).SetTitle("Operation failed").SetMessage(message).SetPositiveButton("OK", (_, _) => { }).Create());
    }
    public override void OnBackPressed() { if (emulatorCancellation != null) { emulatorCancellation.Cancel(); return; } if (!busy) MoveTaskToBack(true); }
    protected override void OnStop()
    {
        base.OnStop();
        if (!pickerActive) ScheduleSave();
        importQueue.Clear(); importDialog?.Dismiss(); emulatorCancellation?.Cancel(); receivedKeys.Clear(); mrCards?.RemoveAllViews(); mrStatus = ""; if (mrOutput != null) mrOutput.Text = "";
        leftApp = !pickerActive; backgrounded = true; backgroundTime = DateTime.UtcNow; backgroundLock?.Cancel(); backgroundLock = new(); LockLater(backgroundLock.Token);
    }
    async void LockLater(CancellationToken token)
    {
        try {
            var keyguard=(KeyguardManager)GetSystemService(KeyguardService);
            bool ShouldClose() => keyguard.IsDeviceLocked || (backgrounded && leftApp && CloseContainerOnLeave);
            while(!ShouldClose()) await Task.Delay(500,token);
            emulatorCancellation?.Cancel();
            while(busy || pickerActive) await Task.Delay(250,token);
            token.ThrowIfCancellationRequested();
            if(!session.IsOpen || !ShouldClose())return;
            if(!session.Dirty){Lock();return;}
            // Never discard edits merely because the phone locked or a provider failed.
            if(HasContainerDestination) SaveContainer(()=>{if(!token.IsCancellationRequested && ShouldClose() && !session.Dirty)Lock();});
            else status="Unsaved container changes retained. Choose a save location before closing.";
        } catch(System.OperationCanceledException) { }
    }
    protected override void OnResume()
    {
        base.OnResume();
        if (startupFailed) return;
        backgroundLock?.Cancel();
        backgrounded = false; leftApp = false;
        if (content != null) Render();
        ScheduleSave();
    }
    protected override void OnDestroy() { autoSaveDelay?.Cancel(); Volatile.Read(ref activeSerial)?.Disconnect(); emulatorCancellation?.Cancel(); importQueue.Clear(); backgroundLock?.Cancel(); if (receiverRegistered) UnregisterReceiver(receiver); if (!busy) session.Dispose(); base.OnDestroy(); }
    sealed class UsbReceiver : BroadcastReceiver
    {
        readonly MainActivity activity;
        public UsbReceiver(MainActivity activity) => this.activity = activity;
        public override void OnReceive(Context context, Intent intent)
        {
            if (intent.Action == Intent.ActionScreenOff) { activity.backgroundLock?.Cancel(); activity.backgroundLock=new(); activity.LockLater(activity.backgroundLock.Token); }
            else if (intent.Action == PermissionAction) activity.status = intent.GetBooleanExtra(UsbManager.ExtraPermissionGranted, false) ? "USB permission granted" : "USB permission denied";
            else if (intent.Action == UsbManager.ActionUsbDeviceDetached)
            {
                var detached = intent.GetParcelableExtra(UsbManager.ExtraDevice) as UsbDevice;
                var active = Volatile.Read(ref activity.activeSerial);
                if (active != null && (detached?.DeviceName == active.DeviceName || !activity.usb.DeviceList.ContainsKey(active.DeviceName)))
                {
                    active.Disconnect();
                    activity.emulatorCancellation?.Cancel();
                }
                if (detached?.DeviceName == activity.usbName || (activity.usbName != null && !activity.usb.DeviceList.ContainsKey(activity.usbName)))
                {
                    activity.usbName = null;
                    activity.status = "USB keyloader disconnected. Reconnect and select the device again.";
                }
            }
            activity.Render();
        }
    }
    sealed class SafeInsets(int horizontal, int vertical) : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View view, WindowInsets insets)
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                var bars = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
                view.SetPadding(horizontal + bars.Left, vertical + bars.Top, horizontal + bars.Right, vertical + bars.Bottom);
            }
            return insets;
        }
    }
}
