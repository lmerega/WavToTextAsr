using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text;
using System.Text.Json;
using Google.Cloud.Speech.V2;
using Google.Protobuf;

namespace WavToGoogleAsr;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private const string WavFolderName = "wavfiles";

    private static readonly string[] SupportedAudioExtensions =
    [
        ".wav",
        ".mp3",
        ".mp4",
        ".m4a",
        ".flac",
        ".ogg",
        ".webm",
        ".aac",
        ".wma"
    ];

    private readonly string baseDir =
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    private readonly AppSettings settings = AppSettings.Load();

    // Menu
    private readonly MenuStrip menuStrip = new();
    private readonly ToolStripMenuItem langMenuItem = new() { ImageScaling = ToolStripItemImageScaling.None };
    private readonly ToolStripMenuItem langHeaderItem = new() { Enabled = false };
    private readonly ToolStripMenuItem helpMenuItem = new()
    {
        Text = "?",
        Alignment = ToolStripItemAlignment.Right
    };

    // Input
    private readonly Label credentialLabel = new();
    private readonly Label audioFolderLabel = new();
    private readonly TextBox credentialTextBox = new();
    private readonly TextBox audioFolderTextBox = new();
    private readonly Button browseCredentialButton = new();
    private readonly Button browseAudioFolderButton = new();

    // File list
    private readonly Label fileCountLabel = new();
    private readonly ListBox fileListBox = new();
    private readonly ProgressBar progressBar = new();

    // Footer
    private readonly Button refreshFilesButton = new();
    private readonly Button convertButton = new();

    // Log
    private readonly TextBox logTextBox = new();

    private readonly CancellationTokenSource formLifetime = new();
    private CancellationTokenSource? conversionCts;

    public MainForm()
    {
        string languageCode = settings.LanguageCode;
        if (!string.IsNullOrWhiteSpace(languageCode))
        {
            I18n.Use(LanguageCatalog.ResolveCulture(languageCode));
        }

        ConfigureWindow();
        BuildLayout();
        BuildMenuStrip();
        ApplyText();
        LoadSettingsIntoControls();
        RefreshFileList();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        formLifetime.Cancel();
        formLifetime.Dispose();
        base.OnFormClosed(e);
    }

    private void ConfigureWindow()
    {
        MinimumSize = new Size(420, 330);
        Size = new Size(500, 400);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        Icon? appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath);
        if (appIcon is not null)
        {
            Icon = appIcon;
        }
    }

    private void BuildMenuStrip()
    {
        langHeaderItem.Font = new Font(Font, FontStyle.Italic);

        langMenuItem.DropDownItems.Add(langHeaderItem);
        langMenuItem.DropDownItems.Add(new ToolStripSeparator());

        foreach (LanguageOption option in LanguageCatalog.Options)
        {
            var item = new ToolStripMenuItem(option.Name)
            {
                Tag = option.Code,
                Image = FlagPainter.CreateImage(option.Code, 28, 16),
                ImageScaling = ToolStripItemImageScaling.None
            };
            item.Click += (_, _) => ChangeLanguage(option.Code);
            langMenuItem.DropDownItems.Add(item);
        }

        helpMenuItem.Click += (_, _) => ShowInstructions();

        menuStrip.Items.Add(langMenuItem);
        menuStrip.Items.Add(helpMenuItem);

        MainMenuStrip = menuStrip;
        Controls.Add(menuStrip);
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(8),
            BackColor = Color.White
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(BuildInputPanel(), 0, 0);
        root.Controls.Add(BuildFilePanel(), 0, 1);
        root.Controls.Add(BuildLogPanel(), 0, 2);
        root.Controls.Add(BuildFooter(), 0, 3);
    }

    private Control BuildInputPanel()
    {
        var inputs = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 3,
            RowCount = 2,
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 8)
        };
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));

        credentialLabel.TextAlign = ContentAlignment.MiddleLeft;
        credentialLabel.Dock = DockStyle.Fill;
        inputs.Controls.Add(credentialLabel, 0, 0);

        credentialTextBox.Dock = DockStyle.Fill;
        inputs.Controls.Add(credentialTextBox, 1, 0);

        browseCredentialButton.Dock = DockStyle.Fill;
        browseCredentialButton.Click += (_, _) => BrowseCredential();
        inputs.Controls.Add(browseCredentialButton, 2, 0);

        audioFolderLabel.TextAlign = ContentAlignment.MiddleLeft;
        audioFolderLabel.Dock = DockStyle.Fill;
        inputs.Controls.Add(audioFolderLabel, 0, 1);

        audioFolderTextBox.Dock = DockStyle.Fill;
        audioFolderTextBox.TextChanged += (_, _) => RefreshFileList();
        inputs.Controls.Add(audioFolderTextBox, 1, 1);

        browseAudioFolderButton.Dock = DockStyle.Fill;
        browseAudioFolderButton.Click += (_, _) => BrowseAudioFolder();
        inputs.Controls.Add(browseAudioFolderButton, 2, 1);

        return inputs;
    }

    private Control BuildFilePanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(0, 0, 0, 6)
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        fileCountLabel.Dock = DockStyle.Fill;
        fileCountLabel.TextAlign = ContentAlignment.MiddleLeft;
        panel.Controls.Add(fileCountLabel, 0, 0);

        fileListBox.Dock = DockStyle.Fill;
        fileListBox.HorizontalScrollbar = true;
        panel.Controls.Add(fileListBox, 0, 1);

        progressBar.Dock = DockStyle.Fill;
        progressBar.Height = 16;
        panel.Controls.Add(progressBar, 0, 2);

        return panel;
    }

    private Control BuildLogPanel()
    {
        logTextBox.Dock = DockStyle.Fill;
        logTextBox.Multiline = true;
        logTextBox.ReadOnly = true;
        logTextBox.ScrollBars = ScrollBars.Vertical;
        logTextBox.Font = new Font("Consolas", 9F);
        return logTextBox;
    }

    private Control BuildFooter()
    {
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0)
        };

        convertButton.AutoSize = true;
        convertButton.MinimumSize = new Size(88, 0);
        convertButton.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold);
        convertButton.Click += async (_, _) =>
        {
            if (conversionCts is not null)
                conversionCts.Cancel();
            else
                await ConvertAsync();
        };
        footer.Controls.Add(convertButton);

        refreshFilesButton.AutoSize = true;
        refreshFilesButton.MinimumSize = new Size(88, 0);
        refreshFilesButton.Click += (_, _) => RefreshFileList();
        footer.Controls.Add(refreshFilesButton);

        return footer;
    }

    private void ApplyText()
    {
        Text = I18n.Get("AppTitle");

        credentialLabel.Text = I18n.Get("CredentialFileLabel");
        audioFolderLabel.Text = I18n.Get("AudioFolderLabel");
        browseCredentialButton.Text = I18n.Get("BrowseButtonShortcut");
        browseAudioFolderButton.Text = I18n.Get("BrowseButtonShortcut");
        refreshFilesButton.Text = I18n.Get("RefreshButtonShortcut");
        convertButton.Text = I18n.Get("ConvertButtonShortcut");
        helpMenuItem.ToolTipText = I18n.Get("InstructionsTab");

        UpdateLanguageMenuItem();
        RefreshFileCountLabel(fileListBox.Items.Count);
        ApplyTheme();
    }

    private void LoadSettingsIntoControls()
    {
        string languageCode = settings.LanguageCode;
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            languageCode = I18n.CurrentCulture.TwoLetterISOLanguageName;
        }

        string normalizedCode = LanguageCatalog.NormalizeCode(languageCode);
        I18n.Use(LanguageCatalog.ResolveCulture(normalizedCode));
        settings.LanguageCode = normalizedCode;
        ApplyText();

        string credPath = File.Exists(settings.CredentialPath)
            ? settings.CredentialPath
            : TryFindDefaultCredentialPath();
        credentialTextBox.Text = credPath;
        credentialTextBox.SelectionStart = credPath.Length;

        string defaultAudioFolder = Path.Combine(baseDir, WavFolderName);
        string audioFolder = Directory.Exists(settings.AudioFolder)
            ? settings.AudioFolder
            : defaultAudioFolder;
        audioFolderTextBox.Text = audioFolder;
        audioFolderTextBox.SelectionStart = audioFolder.Length;
    }

    private string TryFindDefaultCredentialPath()
    {
        try
        {
            return CredentialReader.FindGoogleCredential(baseDir).Path;
        }
        catch
        {
            return "";
        }
    }

    private void BrowseCredential()
    {
        using var dialog = new OpenFileDialog
        {
            InitialDirectory = File.Exists(credentialTextBox.Text)
                ? Path.GetDirectoryName(credentialTextBox.Text)
                : (Directory.Exists(baseDir) ? baseDir : Environment.GetFolderPath(Environment.SpecialFolder.Desktop)),
            Filter = I18n.Get("JsonFileFilter"),
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            credentialTextBox.Text = dialog.FileName;
            credentialTextBox.SelectionStart = dialog.FileName.Length;
            SaveSettings();
        }
    }

    private void UpdateLanguageMenuItem()
    {
        LanguageOption option = LanguageCatalog.Find(I18n.CurrentCulture.TwoLetterISOLanguageName);

        langHeaderItem.Text = I18n.Get("InterfaceLanguageLabel");

        langMenuItem.Text = "  " + option.Name;
        langMenuItem.Image?.Dispose();
        langMenuItem.Image = FlagPainter.CreateImage(option.Code, 28, 16);

        foreach (ToolStripMenuItem item in langMenuItem.DropDownItems.OfType<ToolStripMenuItem>())
        {
            if (item.Tag is string code)
            {
                item.Checked = string.Equals(code, option.Code, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private void BrowseAudioFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            InitialDirectory = Directory.Exists(audioFolderTextBox.Text)
                ? audioFolderTextBox.Text
                : baseDir
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            audioFolderTextBox.Text = dialog.SelectedPath;
            audioFolderTextBox.SelectionStart = dialog.SelectedPath.Length;
            SaveSettings();
        }
    }

    private void RefreshFileList()
    {
        fileListBox.Items.Clear();

        string audioFolder = audioFolderTextBox.Text.Trim();
        if (!Directory.Exists(audioFolder))
        {
            RefreshFileCountLabel(0);
            return;
        }

        foreach (string file in FindAudioFiles(audioFolder))
        {
            fileListBox.Items.Add(file);
        }

        RefreshFileCountLabel(fileListBox.Items.Count);
    }

    private void RefreshFileCountLabel(int count)
    {
        fileCountLabel.Text = I18n.Format("FilesReadyLabel", count);
    }

    private async Task ConvertAsync()
    {
        string credentialPath = credentialTextBox.Text.Trim();
        string audioFolder = audioFolderTextBox.Text.Trim();
        string[] audioFiles = FindAudioFiles(audioFolder);

        if (!File.Exists(credentialPath))
        {
            ShowValidation(I18n.Get("CredentialMissingMessage"));
            return;
        }

        if (!Directory.Exists(audioFolder))
        {
            ShowValidation(I18n.Get("AudioFolderMissingMessage"));
            return;
        }

        if (audioFiles.Length == 0)
        {
            ShowValidation(I18n.Get("NoAudioFilesFound"));
            return;
        }

        conversionCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            conversionCts.Token, formLifetime.Token);

        SetBusy(true);
        logTextBox.Clear();
        progressBar.Minimum = 0;
        progressBar.Maximum = audioFiles.Length;
        progressBar.Value = 0;
        SaveSettings();

        try
        {
            string projectId = CredentialReader.ReadProjectIdFromServiceAccountJson(credentialPath);
            Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", credentialPath);

            AppendLog(I18n.Format("GoogleCredentialsFound", Path.GetFileName(credentialPath)));
            AppendLog(I18n.Format("AudioFilesFound", audioFiles.Length));
            AppendLog(I18n.Get("AutomaticTranscriptionRunning"));
            AppendLog("");

            SpeechClient client = new SpeechClientBuilder
            {
                Endpoint = TranscriptionService.GcpEndpoint
            }.Build();

            RecognitionRunResult result = await TranscriptionService.RunRecognitionAsync(
                client,
                projectId,
                audioFiles,
                ProgressChanged,
                linked.Token);

            string summaryFileName = $"Transcriptions_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt";
            string summaryPath = Path.Combine(baseDir, summaryFileName);
            await TranscriptionService.WriteSummaryFileAsync(summaryPath, result.Entries);

            AppendLog("");
            AppendLog(I18n.Get("RunSummaryTitle"));
            AppendLog(I18n.Format("FilesFoundSummary", audioFiles.Length));
            AppendLog(I18n.Format("FilesSucceededSummary", result.SuccessCount));
            AppendLog(I18n.Format("FilesFailedSummary", result.FailedCount));

            foreach (string failedFile in result.FailedFiles)
            {
                AppendLog($" - {failedFile}");
            }

            AppendLog("");
            AppendLog(I18n.Get("SummaryCreated"));
            AppendLog(summaryPath);
            MessageBox.Show(this, I18n.Get("ConversionCompletedMessage"), I18n.Get("AppTitle"));
        }
        catch (OperationCanceledException)
        {
            AppendLog("");
            AppendLog(I18n.Get("ConversionCancelledMessage"));
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            MessageBox.Show(this, ex.Message, I18n.Get("FatalErrorTitle"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            conversionCts.Dispose();
            conversionCts = null;
            SetBusy(false);
            RefreshFileList();
        }
    }

    private void ProgressChanged(RecognitionProgress progress)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => ProgressChanged(progress));
            return;
        }

        int completed = Math.Clamp(progress.CompletedFiles, 0, progress.TotalFiles);
        progressBar.Maximum = Math.Max(progress.TotalFiles, 1);
        progressBar.Value = Math.Min(completed, progressBar.Maximum);

        if (progress.CurrentFileName is not null)
        {
            fileCountLabel.Text = $"{completed + 1}/{progress.TotalFiles} - {progress.CurrentFileName}";
            if (completed < fileListBox.Items.Count)
            {
                fileListBox.SelectedIndex = completed;
                fileListBox.TopIndex = completed;
            }
        }

        if (!string.IsNullOrWhiteSpace(progress.Message))
        {
            AppendLog(progress.Message);
        }
    }

    private void SetBusy(bool busy)
    {
        langMenuItem.Enabled = !busy;
        credentialTextBox.Enabled = !busy;
        audioFolderTextBox.Enabled = !busy;
        browseCredentialButton.Enabled = !busy;
        browseAudioFolderButton.Enabled = !busy;
        refreshFilesButton.Enabled = !busy;
        convertButton.Text = busy ? I18n.Get("CancelButtonShortcut") : I18n.Get("ConvertButtonShortcut");
    }

    private void ShowValidation(string message)
    {
        AppendLog(message);
        MessageBox.Show(this, message, I18n.Get("AppTitle"), MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void AppendLog(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(message));
            return;
        }

        logTextBox.AppendText(message + Environment.NewLine);
    }

    private void ChangeLanguage(string code)
    {
        I18n.Use(LanguageCatalog.ResolveCulture(code));
        settings.LanguageCode = code;
        SaveSettings();
        ApplyText();
    }

    private void SaveSettings()
    {
        settings.LanguageCode = LanguageCatalog.NormalizeCode(I18n.CurrentCulture.TwoLetterISOLanguageName);
        settings.CredentialPath = credentialTextBox.Text.Trim();
        settings.AudioFolder = audioFolderTextBox.Text.Trim();
        settings.Save();
    }

    private void ApplyTheme()
    {
        Color back = Color.FromArgb(17, 24, 39);
        Color input = Color.FromArgb(55, 65, 81);
        Color fore = Color.FromArgb(243, 244, 246);
        Color muted = Color.FromArgb(209, 213, 219);
        Color button = Color.FromArgb(75, 85, 99);
        Color menuBack = Color.FromArgb(31, 41, 55);
        Color menuHover = Color.FromArgb(55, 65, 81);
        Color menuBorder = Color.FromArgb(55, 65, 81);

        BackColor = back;
        ApplyThemeToControl(this, back, input, fore, muted, button);

        menuStrip.Renderer = new ToolStripProfessionalRenderer(
            new ThemeColorTable(menuBack, menuHover, menuBorder));
        menuStrip.BackColor = menuBack;
        menuStrip.ForeColor = fore;

        foreach (ToolStripItem topItem in menuStrip.Items)
        {
            topItem.ForeColor = fore;
            if (topItem is ToolStripMenuItem mi)
            {
                foreach (ToolStripItem sub in mi.DropDownItems)
                {
                    sub.ForeColor = fore;
                }
            }
        }
    }

    private static void ApplyThemeToControl(
        Control control,
        Color back,
        Color input,
        Color fore,
        Color muted,
        Color button)
    {
        if (control is MenuStrip)
        {
            return;
        }

        control.ForeColor = fore;

        switch (control)
        {
            case TextBox textBox:
                textBox.BackColor = input;
                textBox.ForeColor = fore;
                textBox.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ListBox listBox:
                listBox.BackColor = input;
                listBox.ForeColor = fore;
                break;
            case Button btn:
                btn.BackColor = button;
                btn.ForeColor = fore;
                btn.FlatStyle = FlatStyle.Standard;
                btn.TextAlign = ContentAlignment.MiddleCenter;
                btn.UseMnemonic = true;
                break;
            case Label label:
                label.ForeColor = muted;
                break;
            case TableLayoutPanel:
            case Panel:
                control.BackColor = back;
                break;
            default:
                control.BackColor = back;
                break;
        }

        foreach (Control child in control.Controls)
        {
            ApplyThemeToControl(child, back, input, fore, muted, button);
        }
    }

    private void ShowInstructions()
    {
        MessageBox.Show(
            this,
            BuildInstructionsText(),
            I18n.Get("InstructionsTitle"),
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private string[] FindAudioFiles(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(path => SupportedAudioExtensions.Contains(
                Path.GetExtension(path),
                StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private string BuildInstructionsText()
    {
        StringBuilder sb = new();
        sb.AppendLine(I18n.Get("HelpWhatItDoes"));
        sb.AppendLine();
        sb.AppendLine(I18n.Get("HelpFormCredential"));
        sb.AppendLine(I18n.Get("HelpFormAudioFolder"));
        sb.AppendLine(I18n.Get("HelpFormConvert"));
        sb.AppendLine();
        sb.AppendLine(I18n.Get("HelpSupportedFormats"));
        sb.AppendLine("  .wav .mp3 .mp4 .m4a .flac .ogg .webm .aac .wma");
        sb.AppendLine();
        sb.AppendLine(I18n.Get("HelpResult"));
        sb.AppendLine(I18n.Get("HelpTxtCreated"));
        sb.AppendLine(I18n.Get("HelpSummaryCreated"));
        return sb.ToString();
    }
}

internal sealed class ThemeColorTable : ProfessionalColorTable
{
    private readonly Color _menu;
    private readonly Color _hover;
    private readonly Color _border;

    public ThemeColorTable(Color menu, Color hover, Color border)
    {
        _menu = menu;
        _hover = hover;
        _border = border;
        UseSystemColors = false;
    }

    public override Color MenuStripGradientBegin => _menu;
    public override Color MenuStripGradientEnd => _menu;
    public override Color ToolStripDropDownBackground => _menu;
    public override Color ImageMarginGradientBegin => _menu;
    public override Color ImageMarginGradientMiddle => _menu;
    public override Color ImageMarginGradientEnd => _menu;
    public override Color MenuItemSelected => _hover;
    public override Color MenuItemSelectedGradientBegin => _hover;
    public override Color MenuItemSelectedGradientEnd => _hover;
    public override Color MenuItemPressedGradientBegin => _hover;
    public override Color MenuItemPressedGradientEnd => _hover;
    public override Color MenuItemPressedGradientMiddle => _hover;
    public override Color MenuBorder => _border;
    public override Color MenuItemBorder => Color.Transparent;
    public override Color SeparatorDark => _border;
    public override Color SeparatorLight => _border;
    public override Color CheckBackground => _hover;
    public override Color CheckSelectedBackground => _hover;
    public override Color CheckPressedBackground => _hover;
}

internal static class TranscriptionService
{
    public const string GcpEndpoint = "eu-speech.googleapis.com";
    private const string GcpLocation = "eu";

    private static readonly string[] AutoLanguageCodes = ["auto"];

    public static async Task<RecognitionRunResult> RunRecognitionAsync(
        SpeechClient client,
        string projectId,
        IReadOnlyList<string> audioFiles,
        Action<RecognitionProgress> progress,
        CancellationToken cancellationToken)
    {
        List<RecognitionEntry> entries = [];
        List<string> failedFiles = [];
        int total = audioFiles.Count;

        for (int i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string audioPath = audioFiles[i];
            int current = i + 1;
            string fileName = Path.GetFileName(audioPath);

            void Status(string phase) =>
                progress(new RecognitionProgress(
                    current - 1,
                    total,
                    $"{current}/{total} - {fileName} - {phase}",
                    fileName));

            try
            {
                RecognitionEntry entry = await TranscribeAsync(
                    client,
                    projectId,
                    audioPath,
                    AutoLanguageCodes,
                    Status,
                    cancellationToken);

                string txtPath = Path.ChangeExtension(audioPath, ".txt");
                await File.WriteAllTextAsync(txtPath, entry.Transcript, Encoding.UTF8, cancellationToken);
                entries.Add(entry);

                progress(new RecognitionProgress(
                    current,
                    total,
                    $"{current}/{total} - {fileName} - {I18n.Get("Completed")}"));
            }
            catch (Exception ex)
            {
                failedFiles.Add($"{fileName}: {ex.Message}");
                progress(new RecognitionProgress(
                    current,
                    total,
                    $"{current}/{total} - {fileName} - {I18n.Get("Error")}"));
            }
        }

        return new RecognitionRunResult(entries, failedFiles);
    }

    public static async Task WriteSummaryFileAsync(
        string summaryPath,
        IReadOnlyList<RecognitionEntry> entries)
    {
        StringBuilder sb = new();
        sb.AppendLine("============================================================");
        sb.AppendLine(I18n.Get("TranscriptionSummaryTitle"));
        sb.AppendLine("============================================================");
        sb.AppendLine();

        foreach (RecognitionEntry entry in entries)
        {
            sb.AppendLine(I18n.Format("SummaryFileName", entry.AudioPath));
            sb.AppendLine(I18n.Format("SummaryLanguage", MapLanguageName(entry.DetectedLanguageCode)));
            if (string.IsNullOrWhiteSpace(entry.DetectedLanguageCode))
            {
                sb.AppendLine(I18n.Format("DetectedLanguageCode", FormatBcp47Language(entry.RawDetectedLanguageCode)));
            }

            sb.AppendLine(I18n.Get("MessageText"));
            sb.AppendLine(entry.Transcript);
            sb.AppendLine();
            sb.AppendLine("------------------------------------------------------------");
            sb.AppendLine();
        }

        await File.WriteAllTextAsync(summaryPath, sb.ToString(), Encoding.UTF8);
    }

    private static async Task<RecognitionEntry> TranscribeAsync(
        SpeechClient client,
        string projectId,
        string audioPath,
        IReadOnlyList<string> autoLanguageCodes,
        Action<string>? status,
        CancellationToken cancellationToken)
    {
        status?.Invoke(I18n.Get("ReadingAudioFile"));
        byte[] audioBytes = await File.ReadAllBytesAsync(audioPath, cancellationToken);
        AudioPayload audioPayload = AudioPayload.Prepare(audioPath, audioBytes);

        var config = new RecognitionConfig
        {
            AutoDecodingConfig = new AutoDetectDecodingConfig(),
            Model = "chirp_3"
        };
        config.LanguageCodes.AddRange(autoLanguageCodes);

        var request = new RecognizeRequest
        {
            Recognizer = $"projects/{projectId}/locations/{GcpLocation}/recognizers/_",
            Config = config,
            Content = ByteString.CopyFrom(audioPayload.Bytes)
        };

        status?.Invoke(I18n.Get("UploadDoneRecognitionRunning"));
        Stopwatch sw = Stopwatch.StartNew();
        RecognizeResponse response = await client.RecognizeAsync(request, cancellationToken: cancellationToken);
        sw.Stop();
        status?.Invoke(I18n.Get("ReceivingRecognizedText"));

        StringBuilder sb = new();
        string rawLanguageCode = "";

        foreach (SpeechRecognitionResult result in response.Results)
        {
            SpeechRecognitionAlternative? alt = result.Alternatives.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(alt?.Transcript))
            {
                sb.AppendLine(alt.Transcript.Trim());
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(result.LanguageCode))
            {
                rawLanguageCode = result.LanguageCode;
            }
        }

        string transcript = sb.ToString().Trim();
        if (string.IsNullOrWhiteSpace(transcript))
        {
            transcript = I18n.Get("NoTranscriptionDetected");
        }

        string normalizedLanguage = NormalizeLanguageCode(rawLanguageCode);

        return new RecognitionEntry(
            audioPath,
            normalizedLanguage,
            rawLanguageCode,
            transcript,
            sw.ElapsedMilliseconds);
    }

    private static string NormalizeLanguageCode(string? rawCode)
    {
        if (string.IsNullOrWhiteSpace(rawCode))
        {
            return "";
        }

        string code = rawCode.Trim();

        try
        {
            return CultureInfo.GetCultureInfo(code).Name;
        }
        catch (CultureNotFoundException)
        {
        }

        try
        {
            return CultureInfo.CreateSpecificCulture(code).Name;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    private static string MapLanguageName(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return I18n.Get("NotAvailable");
        }

        try
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(code);
            return UppercaseFirst(culture.NativeName);
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    private static string UppercaseFirst(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (value.Length == 1)
        {
            return value.ToUpperInvariant();
        }

        return char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static string FormatBcp47Language(string? rawCode)
    {
        if (string.IsNullOrWhiteSpace(rawCode))
        {
            return I18n.Get("NotAvailableLower");
        }

        string normalized = NormalizeLanguageCode(rawCode);
        return string.IsNullOrWhiteSpace(normalized) ? rawCode : normalized;
    }

    private sealed record AudioPayload(byte[] Bytes)
    {
        public static AudioPayload Prepare(string audioPath, byte[] audioBytes)
        {
            if (!Path.GetExtension(audioPath).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            {
                return new AudioPayload(audioBytes);
            }

            if (!WavPcm16Converter.TryConvert(audioBytes, out byte[] convertedBytes))
            {
                return new AudioPayload(audioBytes);
            }

            return new AudioPayload(convertedBytes);
        }
    }
}

internal static class WavPcm16Converter
{
    public static bool TryConvert(byte[] wavBytes, out byte[] convertedBytes)
    {
        convertedBytes = wavBytes;

        if (!TryReadPcmWave(wavBytes, out WavPcmInfo? wavInfo) || wavInfo is null)
        {
            return false;
        }

        if (wavInfo.BitsPerSample == 16)
        {
            return false;
        }

        if (wavInfo.BitsPerSample is not (24 or 32))
        {
            return false;
        }

        convertedBytes = BuildPcm16Wave(wavInfo);
        return true;
    }

    private static bool TryReadPcmWave(byte[] wavBytes, out WavPcmInfo? wavInfo)
    {
        wavInfo = null;

        using MemoryStream stream = new(wavBytes, writable: false);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: false);

        if (stream.Length < 44)
        {
            return false;
        }

        if (new string(reader.ReadChars(4)) != "RIFF")
        {
            return false;
        }

        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
        {
            return false;
        }

        ushort audioFormat = 0;
        ushort channels = 0;
        uint sampleRate = 0;
        ushort bitsPerSample = 0;
        byte[]? data = null;

        while (stream.Position + 8 <= stream.Length)
        {
            string chunkId = new(reader.ReadChars(4));
            uint chunkSize = reader.ReadUInt32();

            if (chunkSize > int.MaxValue || stream.Position + chunkSize > stream.Length)
            {
                return false;
            }

            long chunkDataStart = stream.Position;

            if (chunkId == "fmt ")
            {
                if (chunkSize < 16)
                {
                    return false;
                }

                audioFormat = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = reader.ReadUInt32();
                reader.ReadUInt32();
                reader.ReadUInt16();
                bitsPerSample = reader.ReadUInt16();
            }
            else if (chunkId == "data")
            {
                data = reader.ReadBytes((int)chunkSize);
            }

            stream.Position = chunkDataStart + chunkSize;
            if ((chunkSize & 1) == 1 && stream.Position < stream.Length)
            {
                stream.Position++;
            }
        }

        if (audioFormat != 1 || channels == 0 || sampleRate == 0 || bitsPerSample == 0 || data is null)
        {
            return false;
        }

        wavInfo = new WavPcmInfo(channels, sampleRate, bitsPerSample, data);
        return true;
    }

    private static byte[] BuildPcm16Wave(WavPcmInfo wavInfo)
    {
        int sourceBytesPerSample = wavInfo.BitsPerSample / 8;
        int sampleCount = wavInfo.Data.Length / sourceBytesPerSample;
        byte[] pcm16Data = new byte[sampleCount * sizeof(short)];

        for (int i = 0; i < sampleCount; i++)
        {
            int sourceOffset = i * sourceBytesPerSample;
            int sample = wavInfo.BitsPerSample switch
            {
                24 => ReadInt24LittleEndian(wavInfo.Data, sourceOffset),
                32 => BitConverter.ToInt32(wavInfo.Data, sourceOffset),
                _ => throw new InvalidOperationException("Unsupported PCM sample size.")
            };

            short sample16 = (short)Math.Clamp(sample >> (wavInfo.BitsPerSample - 16), short.MinValue, short.MaxValue);
            byte[] bytes = BitConverter.GetBytes(sample16);
            pcm16Data[(i * 2)] = bytes[0];
            pcm16Data[(i * 2) + 1] = bytes[1];
        }

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);

        int blockAlign = wavInfo.Channels * sizeof(short);
        int byteRate = checked((int)wavInfo.SampleRate * blockAlign);
        int riffSize = 36 + pcm16Data.Length;

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(riffSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write(wavInfo.Channels);
        writer.Write(wavInfo.SampleRate);
        writer.Write(byteRate);
        writer.Write((ushort)blockAlign);
        writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcm16Data.Length);
        writer.Write(pcm16Data);
        writer.Flush();

        return stream.ToArray();
    }

    private static int ReadInt24LittleEndian(byte[] bytes, int offset)
    {
        int value = bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16);
        if ((value & 0x00800000) != 0)
        {
            value |= unchecked((int)0xFF000000);
        }

        return value;
    }

    private sealed record WavPcmInfo(ushort Channels, uint SampleRate, ushort BitsPerSample, byte[] Data);
}

internal static class CredentialReader
{
    public static GoogleCredentialInfo FindGoogleCredential(string baseDir)
    {
        string[] jsonFiles = Directory.GetFiles(baseDir, "*.json", SearchOption.TopDirectoryOnly)
            .Where(f =>
                !f.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) &&
                !f.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (jsonFiles.Length == 0)
        {
            throw new FileNotFoundException(I18n.Get("NoJsonFound"));
        }

        foreach (string jsonFile in jsonFiles)
        {
            if (TryReadProjectIdFromServiceAccountJson(jsonFile, out string projectId))
            {
                return new GoogleCredentialInfo(jsonFile, projectId);
            }
        }

        throw new InvalidDataException(I18n.Get("NoValidGoogleCredentialJsonFound"));
    }

    public static string ReadProjectIdFromServiceAccountJson(string jsonPath)
    {
        using FileStream fs = File.OpenRead(jsonPath);
        using JsonDocument doc = JsonDocument.Parse(fs);

        if (!doc.RootElement.TryGetProperty("type", out JsonElement type) ||
            type.GetString() != "service_account")
        {
            throw new InvalidDataException(I18n.Get("JsonNotGoogleServiceAccount"));
        }

        if (!doc.RootElement.TryGetProperty("project_id", out JsonElement projectIdElement))
        {
            throw new InvalidDataException(I18n.Get("ProjectIdNotFound"));
        }

        string? projectId = projectIdElement.GetString();
        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new InvalidDataException(I18n.Get("ProjectIdEmpty"));
        }

        return projectId;
    }

    private static bool TryReadProjectIdFromServiceAccountJson(
        string jsonPath,
        out string projectId)
    {
        projectId = "";

        try
        {
            projectId = ReadProjectIdFromServiceAccountJson(jsonPath);
            return true;
        }
        catch (Exception ex) when (
            ex is JsonException ||
            ex is IOException ||
            ex is UnauthorizedAccessException ||
            ex is InvalidDataException)
        {
            return false;
        }
    }
}

internal static class LanguageCatalog
{
    public static readonly IReadOnlyList<LanguageOption> Options =
    [
        new("en", "English"),
        new("it", "Italiano"),
        new("fr", "Francais"),
        new("de", "Deutsch"),
        new("es", "Espanol"),
        new("pt", "Portugues"),
        new("zh", "中文"),
        new("hi", "Hindi"),
        new("ar", "Arabic"),
        new("ja", "Japanese"),
        new("ko", "Korean"),
        new("ru", "Russian"),
        new("tr", "Turkce"),
        new("pl", "Polski"),
        new("nl", "Nederlands")
    ];

    private static readonly Dictionary<string, string> SupportedCultures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ar"] = "ar",
        ["de"] = "de",
        ["en"] = "en",
        ["es"] = "es",
        ["fr"] = "fr",
        ["hi"] = "hi",
        ["it"] = "it",
        ["ja"] = "ja",
        ["ko"] = "ko",
        ["nl"] = "nl",
        ["pl"] = "pl",
        ["pt"] = "pt",
        ["ru"] = "ru",
        ["tr"] = "tr",
        ["zh"] = "zh-Hans"
    };

    public static CultureInfo ResolveCulture(string? requestedLanguage)
    {
        if (string.IsNullOrWhiteSpace(requestedLanguage))
        {
            return CultureInfo.GetCultureInfo("en");
        }

        string normalizedLanguage = requestedLanguage.Trim().Replace('_', '-').ToLowerInvariant();
        string languagePrefix = normalizedLanguage.Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (SupportedCultures.TryGetValue(languagePrefix, out string? cultureName))
        {
            return CultureInfo.GetCultureInfo(cultureName);
        }

        return CultureInfo.GetCultureInfo("en");
    }

    public static string NormalizeCode(string code)
    {
        string prefix = code.Trim().Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "en";
        return SupportedCultures.ContainsKey(prefix) ? prefix : "en";
    }

    public static LanguageOption Find(string code)
    {
        string normalized = NormalizeCode(code);
        return Options.FirstOrDefault(option => option.Code.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            ?? Options.First(option => option.Code == "en");
    }
}

internal sealed class AppSettings
{
    public string LanguageCode { get; set; } = "";
    public string CredentialPath { get; set; } = "";
    public string AudioFolder { get; set; } = "";

    private static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WavToTextAsr",
            "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(SettingsPath, Encoding.UTF8);
            return JsonSerializer.Deserialize<AppSettings>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json, Encoding.UTF8);
    }
}

internal static class I18n
{
    private static readonly ResourceManager ResourceManager = new(
        "WavToGoogleAsr.Resources.Messages",
        Assembly.GetExecutingAssembly());

    public static CultureInfo CurrentCulture { get; private set; } = CultureInfo.GetCultureInfo("en");

    public static void Use(CultureInfo selectedCulture)
    {
        CurrentCulture = selectedCulture;
        CultureInfo.DefaultThreadCurrentCulture = selectedCulture;
        CultureInfo.DefaultThreadCurrentUICulture = selectedCulture;
    }

    public static string Get(string key) =>
        ResourceManager.GetString(key, CurrentCulture) ?? key;

    public static string Format(string key, params object[] values) =>
        string.Format(CurrentCulture, Get(key), values);
}

internal static class FlagPainter
{
    public static Bitmap CreateImage(string code, int width, int height)
    {
        Bitmap bitmap = new(width, height);
        using Graphics graphics = Graphics.FromImage(bitmap);
        Draw(graphics, new Rectangle(0, 0, width - 1, height - 1), code);
        return bitmap;
    }

    public static void Draw(Graphics graphics, Rectangle bounds, string code)
    {
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using SolidBrush border = new(Color.FromArgb(148, 163, 184));
        graphics.FillRectangle(border, bounds);

        Rectangle inner = Rectangle.Inflate(bounds, -1, -1);
        using System.Drawing.Drawing2D.GraphicsPath clip = RoundedRect(inner, 2);
        Region oldClip = graphics.Clip;
        graphics.SetClip(clip);

        switch (code)
        {
            case "it":
                Vertical(graphics, inner, Color.FromArgb(0, 146, 70), Color.White, Color.FromArgb(206, 43, 55));
                break;
            case "fr":
                Vertical(graphics, inner, Color.FromArgb(0, 85, 164), Color.White, Color.FromArgb(239, 65, 53));
                break;
            case "de":
                Horizontal(graphics, inner, Color.Black, Color.FromArgb(221, 0, 0), Color.FromArgb(255, 206, 0));
                break;
            case "es":
                Horizontal(graphics, inner, Color.FromArgb(170, 21, 27), Color.FromArgb(241, 191, 0), Color.FromArgb(170, 21, 27), middleWeight: 2);
                break;
            case "pt":
                VerticalRatio(graphics, inner, Color.FromArgb(0, 102, 0), Color.FromArgb(255, 0, 0), 2, 3);
                DrawDisc(graphics, inner, Color.FromArgb(255, 204, 0), 0.42f, 0.5f, 0.17f);
                break;
            case "zh":
                Fill(graphics, inner, Color.FromArgb(222, 41, 16));
                DrawDisc(graphics, inner, Color.FromArgb(255, 222, 0), 0.25f, 0.32f, 0.11f);
                break;
            case "hi":
                Horizontal(graphics, inner, Color.FromArgb(255, 153, 51), Color.White, Color.FromArgb(19, 136, 8));
                DrawRing(graphics, inner, Color.FromArgb(0, 0, 128), 0.5f, 0.5f, 0.12f);
                break;
            case "ar":
                Fill(graphics, inner, Color.FromArgb(0, 108, 53));
                DrawDisc(graphics, inner, Color.White, 0.48f, 0.5f, 0.14f);
                DrawDisc(graphics, inner, Color.FromArgb(0, 108, 53), 0.53f, 0.5f, 0.12f);
                break;
            case "ja":
                Fill(graphics, inner, Color.White);
                DrawDisc(graphics, inner, Color.FromArgb(188, 0, 45), 0.5f, 0.5f, 0.2f);
                break;
            case "ko":
                Fill(graphics, inner, Color.White);
                DrawHalfDisc(graphics, inner, Color.FromArgb(205, 46, 58), Color.FromArgb(0, 71, 160));
                break;
            case "ru":
                Horizontal(graphics, inner, Color.White, Color.FromArgb(0, 57, 166), Color.FromArgb(213, 43, 30));
                break;
            case "tr":
                Fill(graphics, inner, Color.FromArgb(227, 10, 23));
                DrawDisc(graphics, inner, Color.White, 0.43f, 0.5f, 0.16f);
                DrawDisc(graphics, inner, Color.FromArgb(227, 10, 23), 0.48f, 0.5f, 0.13f);
                DrawDisc(graphics, inner, Color.White, 0.62f, 0.5f, 0.05f);
                break;
            case "pl":
                Horizontal(graphics, inner, Color.White, Color.White, Color.FromArgb(220, 20, 60), topWeight: 1, middleWeight: 0, bottomWeight: 1);
                break;
            case "nl":
                Horizontal(graphics, inner, Color.FromArgb(174, 28, 40), Color.White, Color.FromArgb(33, 70, 139));
                break;
            default:
                Fill(graphics, inner, Color.FromArgb(1, 33, 105));
                DrawCross(graphics, inner);
                break;
        }

        graphics.Clip = oldClip;
        using Pen pen = new(Color.FromArgb(100, 116, 139));
        graphics.DrawRectangle(pen, bounds);
    }

    private static void Fill(Graphics graphics, Rectangle rect, Color color)
    {
        using SolidBrush brush = new(color);
        graphics.FillRectangle(brush, rect);
    }

    private static void Vertical(Graphics graphics, Rectangle rect, Color left, Color middle, Color right)
    {
        int w = rect.Width / 3;
        Fill(graphics, new Rectangle(rect.Left, rect.Top, w, rect.Height), left);
        Fill(graphics, new Rectangle(rect.Left + w, rect.Top, w, rect.Height), middle);
        Fill(graphics, new Rectangle(rect.Left + (w * 2), rect.Top, rect.Width - (w * 2), rect.Height), right);
    }

    private static void VerticalRatio(Graphics graphics, Rectangle rect, Color left, Color right, int leftWeight, int rightWeight)
    {
        int leftWidth = rect.Width * leftWeight / (leftWeight + rightWeight);
        Fill(graphics, new Rectangle(rect.Left, rect.Top, leftWidth, rect.Height), left);
        Fill(graphics, new Rectangle(rect.Left + leftWidth, rect.Top, rect.Width - leftWidth, rect.Height), right);
    }

    private static void Horizontal(
        Graphics graphics,
        Rectangle rect,
        Color top,
        Color middle,
        Color bottom,
        int topWeight = 1,
        int middleWeight = 1,
        int bottomWeight = 1)
    {
        int total = topWeight + middleWeight + bottomWeight;
        int topHeight = rect.Height * topWeight / total;
        int middleHeight = rect.Height * middleWeight / total;
        Fill(graphics, new Rectangle(rect.Left, rect.Top, rect.Width, topHeight), top);
        if (middleHeight > 0)
        {
            Fill(graphics, new Rectangle(rect.Left, rect.Top + topHeight, rect.Width, middleHeight), middle);
        }

        Fill(graphics, new Rectangle(rect.Left, rect.Top + topHeight + middleHeight, rect.Width, rect.Height - topHeight - middleHeight), bottom);
    }

    private static void DrawDisc(Graphics graphics, Rectangle rect, Color color, float x, float y, float radius)
    {
        using SolidBrush brush = new(color);
        float d = rect.Height * radius * 2;
        float cx = rect.Left + rect.Width * x;
        float cy = rect.Top + rect.Height * y;
        graphics.FillEllipse(brush, cx - d / 2, cy - d / 2, d, d);
    }

    private static void DrawRing(Graphics graphics, Rectangle rect, Color color, float x, float y, float radius)
    {
        using Pen pen = new(color, 1.5f);
        float d = rect.Height * radius * 2;
        float cx = rect.Left + rect.Width * x;
        float cy = rect.Top + rect.Height * y;
        graphics.DrawEllipse(pen, cx - d / 2, cy - d / 2, d, d);
    }

    private static void DrawHalfDisc(Graphics graphics, Rectangle rect, Color top, Color bottom)
    {
        float d = rect.Height * 0.5f;
        float x = rect.Left + rect.Width * 0.5f - d / 2;
        float y = rect.Top + rect.Height * 0.5f - d / 2;
        using SolidBrush topBrush = new(top);
        using SolidBrush bottomBrush = new(bottom);
        graphics.FillPie(topBrush, x, y, d, d, 180, 180);
        graphics.FillPie(bottomBrush, x, y, d, d, 0, 180);
    }

    private static void DrawCross(Graphics graphics, Rectangle rect)
    {
        using Pen white = new(Color.White, 5);
        using Pen red = new(Color.FromArgb(200, 16, 46), 3);
        graphics.DrawLine(white, rect.Left, rect.Top, rect.Right, rect.Bottom);
        graphics.DrawLine(white, rect.Right, rect.Top, rect.Left, rect.Bottom);
        graphics.DrawLine(white, rect.Left + rect.Width / 2, rect.Top, rect.Left + rect.Width / 2, rect.Bottom);
        graphics.DrawLine(white, rect.Left, rect.Top + rect.Height / 2, rect.Right, rect.Top + rect.Height / 2);
        graphics.DrawLine(red, rect.Left + rect.Width / 2, rect.Top, rect.Left + rect.Width / 2, rect.Bottom);
        graphics.DrawLine(red, rect.Left, rect.Top + rect.Height / 2, rect.Right, rect.Top + rect.Height / 2);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        System.Drawing.Drawing2D.GraphicsPath path = new();
        int diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed record LanguageOption(string Code, string Name);

internal sealed record RecognitionEntry(
    string AudioPath,
    string DetectedLanguageCode,
    string RawDetectedLanguageCode,
    string Transcript,
    long ElapsedMilliseconds);

internal sealed record RecognitionRunResult(
    IReadOnlyList<RecognitionEntry> Entries,
    IReadOnlyList<string> FailedFiles)
{
    public int SuccessCount => Entries.Count;
    public int FailedCount => FailedFiles.Count;
}

internal sealed record RecognitionProgress(
    int CompletedFiles,
    int TotalFiles,
    string Message,
    string? CurrentFileName = null);

internal sealed record GoogleCredentialInfo(
    string Path,
    string ProjectId);
