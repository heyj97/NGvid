using System.IO;
using NGL.Core;
using NGL.Export;
using NGL.IO;

namespace NGL.App;

/// <summary>프로그램 설정, 자동 백업과 복구, 프록시.</summary>
public partial class MainViewModel
{
    private AppSettings _settings = AppSettingsStore.Load();
    private DateTime _lastBackup = DateTime.UtcNow;
    private CancellationTokenSource? _proxyRun;

    public AppSettings Settings => _settings;

    /// <summary>설정을 저장하고 바로 적용한다.</summary>
    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        try
        {
            AppSettingsStore.Save(settings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusText = "설정을 저장하지 못했습니다: " + e.Message;
        }

        _history.Limit = settings.UndoLimit;
        RebuildEngine(); // 프록시 사용 여부가 바뀌었을 수 있다
    }

    // ───── 자동 백업 ─────

    /// <summary>마지막 백업에서 정해 둔 시간이 지났으면 백업한다. 화면을 그릴 때마다 불린다.</summary>
    private void BackupIfDue()
    {
        if (_settings.BackupMinutes > 0 && DateTime.UtcNow - _lastBackup >= TimeSpan.FromMinutes(_settings.BackupMinutes)) Backup();
    }

    private void Backup()
    {
        _lastBackup = DateTime.UtcNow;
        if (_folder is null || _settings.BackupMinutes == 0) return;
        try
        {
            ProjectBackup.Create(_folder, _settings.BackupCount);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusText = "백업하지 못했습니다: " + e.Message;
        }
    }

    public IReadOnlyList<string> Backups() => _folder is null ? [] : ProjectBackup.List(_folder);

    /// <summary>프로젝트를 백업의 상태로 되돌리고 다시 연다. 되돌리기 전의 상태도 백업으로 남는다.</summary>
    public void RestoreBackup(string backupPath)
    {
        if (_folder is not { } folder) return;
        try
        {
            ProjectBackup.Restore(folder, backupPath, _settings.BackupCount);
            Load(ProjectFolder.Load(folder), folder);
            StatusText = $"백업에서 복구했습니다 ({ProjectBackup.TimeOf(backupPath):yyyy-MM-dd HH:mm:ss}).";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            StatusText = "복구하지 못했습니다: " + e.Message;
        }
    }

    // ───── 프록시 ─────

    /// <summary>영상 원본의 프록시를 뒤에서 만든다. 다 되면 미리보기가 프록시를 쓰기 시작한다.</summary>
    public async Task BuildProxy(AssetItem item)
    {
        if (_folder is not { } folder || item.Asset.Kind != MediaKind.Video || item.IsMissing) return;
        if (_proxyRun is not null)
        {
            StatusText = "다른 프록시를 만드는 중입니다.";
            return;
        }

        var run = _proxyRun = new CancellationTokenSource();
        var progress = new Progress<double>(value => StatusText = $"프록시 만드는 중: {item.Name} {value:P0}");
        try
        {
            await Task.Run(() => ProxyBuilder.Build(folder, item.Asset, progress, run.Token));
            StatusText = $"프록시를 만들었습니다: {item.Name}";
            if (_folder == folder) RebuildEngine();
        }
        catch (OperationCanceledException)
        {
            StatusText = "프록시 만들기를 취소했습니다.";
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            StatusText = "프록시를 만들지 못했습니다: " + e.Message;
        }
        finally
        {
            run.Dispose();
            _proxyRun = null;
        }
    }

    /// <summary>미리보기 화면을 읽을 파일.</summary>
    private string VideoFile(string folder, MediaAsset asset) =>
        _settings.UseProxies ? ProxyBuilder.PreviewFile(folder, asset) : ProjectPaths.AssetFile(folder, asset);
}
