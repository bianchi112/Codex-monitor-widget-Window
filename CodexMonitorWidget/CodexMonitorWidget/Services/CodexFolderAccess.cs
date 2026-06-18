using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using CodexMonitorWidget.Models;

namespace CodexMonitorWidget.Services;

public sealed class CodexFolderAccess
{
    private const string SettingsFileName = "settings.json";
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CodexMonitorWidget");

    private string? _codexHomePath;

    public string? AccessError { get; private set; }
    public CodexStoreLifecycle Lifecycle { get; } = new();

    public bool HasAccess => _codexHomePath != null;

    public string? SessionsPath =>
        _codexHomePath == null ? null : Path.Combine(_codexHomePath, "sessions");

    public string DisplayPath => SessionsPath ?? "~/.codex/sessions";
    public string ExpectedCodexPath => CodexPathHelper.DefaultHomePath;
    public string ExpectedSessionsPath => CodexPathHelper.DefaultSessionsPath;
    public bool IsExpectedFolderAvailable => CodexPathHelper.SessionsExists;

    public void Shutdown() => Lifecycle.Shutdown();

    public void RestoreSavedAccess()
    {
        AccessError = null;

        var savedPath = LoadSavedPath();
        if (!string.IsNullOrEmpty(savedPath))
        {
            try
            {
                Activate(savedPath);
                return;
            }
            catch
            {
                ClearSavedAccess();
                AccessError = "저장된 폴더 접근 권한이 만료되었습니다. 다시 선택해 주세요.";
            }
        }

        if (CodexPathHelper.SessionsExists)
        {
            try
            {
                Activate(CodexPathHelper.DefaultHome);
            }
            catch
            {
                AccessError = null;
            }
        }
    }

    public void RequestDefaultFolderAccess() => RequestFolderAccess(openAtDefaultLocation: true);

    public void RequestFolderAccess(bool openAtDefaultLocation = false)
    {
        AccessError = null;

        using var dialog = new FolderBrowserDialog
        {
            Description = BuildFolderDialogDescription(openAtDefaultLocation),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (openAtDefaultLocation && CodexPathHelper.HomeExists)
            dialog.SelectedPath = CodexPathHelper.DefaultHome;
        else if (CodexPathHelper.HomeExists)
            dialog.SelectedPath = Path.GetDirectoryName(CodexPathHelper.DefaultHome) ?? CodexPathHelper.DefaultHome;
        else
            dialog.SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (dialog.ShowDialog() != DialogResult.OK || string.IsNullOrEmpty(dialog.SelectedPath))
            return;

        ActivateSelectedFolder(dialog.SelectedPath);
    }

    public void RevealExpectedFolderInExplorer()
    {
        AccessError = null;

        if (CodexPathHelper.HomeExists)
        {
            OpenInExplorer(CodexPathHelper.DefaultHome);
            return;
        }

        OpenInExplorer(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        AccessError = """
            아직 .codex 폴더가 없습니다.
            탐색기에서 Codex를 한 번 실행한 뒤 다시 시도해 주세요.
            """;
    }

    private void ActivateSelectedFolder(string selectedPath)
    {
        try
        {
            var codexHome = ResolveCodexHome(selectedPath);
            SavePath(codexHome);
            Activate(codexHome);
            AccessError = null;
        }
        catch (CodexFolderAccessException ex)
        {
            AccessError = ex.Message;
        }
        catch
        {
            AccessError = "폴더 접근 권한 저장에 실패했습니다.";
        }
    }

    private void Activate(string codexHomePath)
    {
        var sessions = Path.Combine(codexHomePath, "sessions");
        if (!Directory.Exists(sessions))
            throw new CodexFolderAccessException(CodexFolderAccessError.MissingSessionsFolder);

        _codexHomePath = codexHomePath;
    }

    private static string ResolveCodexHome(string selectedPath)
    {
        var normalized = Path.GetFullPath(selectedPath);
        var folderName = Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (folderName == ".codex")
        {
            var sessions = Path.Combine(normalized, "sessions");
            if (!Directory.Exists(sessions))
                throw new CodexFolderAccessException(CodexFolderAccessError.MissingSessionsFolder);
            return normalized;
        }

        var sessionsAtSelection = Path.Combine(normalized, "sessions");
        if (Directory.Exists(sessionsAtSelection))
            return normalized;

        if (folderName == "sessions" && Directory.Exists(normalized))
            return Directory.GetParent(normalized)?.FullName
                   ?? throw new CodexFolderAccessException(CodexFolderAccessError.MissingSessionsFolder);

        throw new CodexFolderAccessException(CodexFolderAccessError.MissingSessionsFolder);
    }

    private void ClearSavedAccess()
    {
        _codexHomePath = null;
        var settingsPath = GetSettingsPath();
        if (File.Exists(settingsPath))
            File.Delete(settingsPath);
    }

    private static void SavePath(string codexHomePath)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var settings = LoadSettingsDictionary();
        settings["codexHomePath"] = codexHomePath;
        var json = JsonSerializer.Serialize(settings);
        File.WriteAllText(GetSettingsPath(), json);
    }

    private static string? LoadSavedPath()
    {
        var settings = LoadSettingsDictionary();
        if (settings.TryGetValue("codexHomePath", out var value) && value is JsonElement element &&
            element.ValueKind == JsonValueKind.String)
            return element.GetString();

        return null;
    }

    private static Dictionary<string, object?> LoadSettingsDictionary()
    {
        var settingsPath = GetSettingsPath();
        if (!File.Exists(settingsPath))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            return doc.RootElement.EnumerateObject()
                .ToDictionary(property => property.Name, object? (property) => property.Value.Clone());
        }
        catch
        {
            return [];
        }
    }

    private static string GetSettingsPath() => Path.Combine(SettingsDirectory, SettingsFileName);

    private static string BuildFolderDialogDescription(bool openAtDefaultLocation)
    {
        if (openAtDefaultLocation && CodexPathHelper.HomeExists)
        {
            return $"""
                아래 경로의 ".codex" 폴더를 선택해 주세요.
                폴더가 이미 보이면 "확인"만 누르면 됩니다.

                {CodexPathHelper.DefaultHomePath}
                """;
        }

        if (CodexPathHelper.HomeExists)
        {
            return $"""
                ".codex" 폴더를 선택해 주세요.
                숨김 폴더가 안 보이면 탐색기 옵션에서 숨김 항목 표시를 켜 주세요.

                {CodexPathHelper.DefaultHomePath}
                """;
        }

        return $"""
            Codex 폴더를 찾을 수 없습니다.
            Codex를 한 번 실행한 뒤, 홈 폴더의 ".codex" 를 선택해 주세요.

            예상 경로: {CodexPathHelper.DefaultHomePath}
            """;
    }

    private static void OpenInExplorer(string path)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private sealed class CodexFolderAccessException : Exception
    {
        public CodexFolderAccessException(CodexFolderAccessError error)
            : base(CodexFolderAccessErrorMessages.GetMessage(error))
        {
        }
    }
}
