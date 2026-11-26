using System.IO.Compression;
using System.Text.Json;


namespace BenchBook.Core.Updates;

public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What the updater left for the next launch to report.</summary>
public sealed record UpdateResult(int Format, bool Installed, string Version, string? Message, string? OldFolder)
{
    public const int CurrentFormat = 1;
}

/// <summary>
/// What the updater is asked to do: <see cref="From"/> is the new version's
/// unpacked folder, <see cref="Target"/> the folder BenchBook runs from.
/// </summary>
public sealed record UpdatePlan(int Format, string Version, string From, string Target, int ProcessId)
{
    public const int CurrentFormat = 1;
}

/// <summary>
/// Installs an update (behaviour.md, "Updates"). The zip is downloaded into
/// "BenchBook Update" in the app folder, its signature checked, and it's
/// unpacked there. The new version's BenchBook.exe then runs from there as
/// the updater, with <c>--install-update "plan.json"</c>: it waits for the
/// old one to close, copies itself beside the app's folder, swaps the two
/// folders, and starts the new version. The library is never in the app's
/// folder, so it isn't touched.
/// <para>Every later version must keep accepting <c>--install-update</c>
/// with a format 1 plan, because that's how the version before it hands over.</para>
/// </summary>
public static class UpdateInstaller
{
    public const string InstallArgument = "--install-update";
    public const string ExeName = "BenchBook.exe";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static string StagingFolder(LibraryLocation location) => Path.Combine(location.AppFolder, "BenchBook Update");

    private static string PlanPath(string staging) => Path.Combine(staging, "Plan.json");

    private static string ResultPath(string staging) => Path.Combine(staging, "Result.json");

    /// <summary>
    /// Why BenchBook can't replace the folder it runs from, or null if it can.
    /// It needs to make a folder beside it and rename it, and the folder mustn't
    /// hold the library or be inside it.
    /// </summary>
    public static string? CantReplace(string appFolder, LibraryLocation location)
    {
        var target = Full(appFolder);
        var parent = Path.GetDirectoryName(target);
        if (parent is null || !File.Exists(Path.Combine(target, ExeName)))
            return "BenchBook isn't running from a folder it can update.";
        var library = Full(location.AppFolder);
        if (IsInside(library, target) || IsInside(target, library))
            return $"BenchBook is running from a folder that holds its library, “{target}”, so replacing it isn't safe.";
        try
        {
            var probe = Path.Combine(parent, $".BenchBook-update-check-{Guid.NewGuid():N}");
            Directory.CreateDirectory(probe);
            Directory.Delete(probe);
            var file = Path.Combine(target, $".BenchBook-update-check-{Guid.NewGuid():N}");
            File.WriteAllBytes(file, []);
            File.Delete(file);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"BenchBook can't change the folder it's in, “{target}”.";
        }
    }

    /// <summary>
    /// Downloads the zip, checks it's the one the feed signed, and unpacks it.
    /// Returns the new version's BenchBook folder, ready for <see cref="Schedule"/>.
    /// </summary>
    public static async Task<string> DownloadAsync(HttpClient http, UpdateItem item, string installedVersion, LibraryLocation location, string publicKey, IProgress<double>? progress = null, CancellationToken cancellation = default)
    {
        var staging = StagingFolder(location);
        DeleteFolder(staging);
        Directory.CreateDirectory(staging);
        var zip = Path.Combine(staging, "Update.zip");
        var partial = zip + ".partial";

        try
        {
            using var request = UpdateFeed.Request(item.Download, installedVersion);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? item.Length;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellation))
            await using (var destination = File.Create(partial))
            {
                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellation);
                    copied += read;
                    if (total > 0) progress?.Report(Math.Min(1, (double)copied / total));
                }
            }
            File.Move(partial, zip);
        }
        catch (Exception error) when (error is HttpRequestException or IOException || (error is TaskCanceledException && !cancellation.IsCancellationRequested))
        {
            throw new UpdateException("The update couldn't be downloaded. Check that this PC is online, then try again.", error);
        }

        if (item.Length > 0 && new FileInfo(zip).Length != item.Length)
            throw new UpdateException("The download isn't the size the list of updates gave, so it wasn't installed.");
        if (!UpdateFeed.IsSignedFile(publicKey, item.Signature, zip))
            throw new UpdateException("The download isn't signed with BenchBook's update key, so it wasn't installed.");

        var unpacked = Path.Combine(staging, "New");
        try
        {
            // ExtractToDirectory refuses entries that would land outside the folder.
            ZipFile.ExtractToDirectory(zip, unpacked);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new UpdateException("The update couldn't be unpacked.", error);
        }
        File.Delete(zip);
        var folder = Path.Combine(unpacked, "BenchBook");
        if (!File.Exists(Path.Combine(folder, ExeName)))
            throw new UpdateException("The update doesn't have BenchBook in it.");
        return folder;
    }

    /// <summary>Writes the plan, and returns the updater to start and its arguments.</summary>
    public static (string Exe, string[] Arguments) Schedule(LibraryLocation location, string version, string newFolder, string appFolder, int processId)
    {
        var staging = StagingFolder(location);
        var plan = new UpdatePlan(UpdatePlan.CurrentFormat, version, Full(newFolder), Full(appFolder), processId);
        File.Delete(ResultPath(staging));
        File.WriteAllBytes(PlanPath(staging), JsonSerializer.SerializeToUtf8Bytes(plan, Json));
        return (Path.Combine(plan.From, ExeName), [InstallArgument, PlanPath(staging)]);
    }

    /// <summary>
    /// Runs in the updater: replaces the app's folder with the new one, and
    /// returns the folder to start BenchBook from (the new one, or the old one
    /// if it couldn't be replaced). The result is left for the next launch.
    /// </summary>
    public static string Install(string planPath, LibraryLocation location, Func<int, bool> waitForExit)
    {
        var staging = Path.GetDirectoryName(Full(planPath))!;
        UpdatePlan? plan = null;
        try
        {
            plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllBytes(planPath), Json)
                ?? throw new UpdateException("The update's plan is empty.");
            if (plan.Format > UpdatePlan.CurrentFormat) throw new UpdateException("The update's plan is from a newer BenchBook.");
            if (!waitForExit(plan.ProcessId)) throw new UpdateException("BenchBook didn't close.");
            if (CantReplace(plan.Target, location) is { } reason) throw new UpdateException(reason);
            if (!File.Exists(Path.Combine(plan.From, ExeName))) throw new UpdateException("The update doesn't have BenchBook in it.");

            var old = Replace(plan.From, plan.Target);
            WriteResult(staging, new UpdateResult(UpdateResult.CurrentFormat, true, plan.Version, null, old));
        }
        catch (Exception error) when (error is UpdateException or IOException or UnauthorizedAccessException or JsonException)
        {
            WriteResult(staging, new UpdateResult(UpdateResult.CurrentFormat, false, plan?.Version ?? "", error.Message, null));
        }
        finally
        {
            TryDelete(planPath);
        }
        return plan?.Target ?? "";
    }

    /// <summary>
    /// Copies the new version beside the old, then swaps them. The old folder is
    /// kept until the next launch, and put back if the swap fails.
    /// </summary>
    internal static string Replace(string from, string target)
    {
        target = Full(target);
        var parent = Path.GetDirectoryName(target)!;
        var name = Path.GetFileName(target);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var incoming = Path.Combine(parent, $"{name}.new-{tag}");
        var outgoing = Path.Combine(parent, $"{name}.old-{tag}");
        try
        {
            CopyFolder(from, incoming);
            // Something still open in the folder, such as an Explorer window, can hold it for a moment.
            Retry(() => Directory.Move(target, outgoing));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DeleteFolder(incoming);
            throw new UpdateException($"BenchBook's folder, “{target}”, couldn't be replaced. Close anything using it, then try again. ({error.Message})", error);
        }
        try
        {
            Retry(() => Directory.Move(incoming, target));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Directory.Move(outgoing, target);
            DeleteFolder(incoming);
            throw new UpdateException($"BenchBook's folder, “{target}”, couldn't be replaced. ({error.Message})", error);
        }
        return outgoing;
    }

    /// <summary>
    /// At launch: removes what the last update left, and returns its result if
    /// there was one. A folder still in use is left for the next launch.
    /// </summary>
    public static UpdateResult? FinishPending(LibraryLocation location)
    {
        var staging = StagingFolder(location);
        if (!Directory.Exists(staging)) return null;
        UpdateResult? result = null;
        try
        {
            if (File.Exists(ResultPath(staging)))
                result = JsonSerializer.Deserialize<UpdateResult>(File.ReadAllBytes(ResultPath(staging)), Json);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        // Only the folder the updater moved aside, never anything else beside the app.
        if (result is { Installed: true, OldFolder: { } old } && IsOldFolder(old))
            DeleteFolder(old);
        DeleteFolder(staging);
        return result;
    }

    private static bool IsOldFolder(string path) =>
        Path.GetFileName(Full(path)).Contains(".old-", StringComparison.Ordinal) && File.Exists(Path.Combine(path, ExeName));

    private static void WriteResult(string staging, UpdateResult result)
    {
        try
        {
            Directory.CreateDirectory(staging);
            File.WriteAllBytes(ResultPath(staging), JsonSerializer.SerializeToUtf8Bytes(result, Json));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception error) when (attempt < 20 && error is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }
    }

    private static void DeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsInside(string path, string folder) =>
        string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
