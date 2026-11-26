using System.Text;
using BenchBook.Core.Updates;

// The Windows update key and feeds (CLAUDE.md, "Releasing").
//
//   dotnet run --project build\UpdateFeed -- keys "<private key file>"
//       Makes a new key pair. The private key goes into the file, which must not
//       exist yet; only the public key is printed. Keep the file safe and out of
//       the repo: it's what the release build signs with.
//
//   dotnet run --project build\UpdateFeed -- feeds <version> <notes.md> <artifacts folder> <owner/repo>
//       Signs each zip in the folder and writes its processor's feed and the
//       feed's .signature beside it. The private key comes from the
//       BENCHBOOK_UPDATE_PRIVATE_KEY environment variable, and must match the
//       public key built into the app.

const string KeyVariable = "BENCHBOOK_UPDATE_PRIVATE_KEY";
string[] Runtimes = ["win-x64", "win-arm64"];

try
{
    return args switch
    {
        ["keys", var file] => Keys(file),
        ["feeds", var version, var notes, var folder, var repository] => Feeds(version, notes, folder, repository),
        _ => Usage(),
    };
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("Use: keys <private key file>, or feeds <version> <notes.md> <artifacts folder> <owner/repo>.");
    return 2;
}

static int Keys(string file)
{
    if (File.Exists(file))
    {
        Console.Error.WriteLine($"“{file}” already exists. Choose a new file, so an existing key isn't lost.");
        return 1;
    }
    var (privateKey, publicKey) = UpdateFeed.NewKeys();
    using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write))
    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        writer.Write(privateKey);
    Console.WriteLine($"The private key is in “{Path.GetFullPath(file)}”.");
    Console.WriteLine("Add it as the BENCHBOOK_UPDATE_PRIVATE_KEY secret on GitHub, then keep a copy somewhere safe and offline.");
    Console.WriteLine();
    Console.WriteLine("The public key, for UpdateFeed.PublicKey:");
    Console.WriteLine(publicKey);
    return 0;
}

int Feeds(string version, string notesPath, string folder, string repository)
{
    var privateKey = Environment.GetEnvironmentVariable(KeyVariable);
    if (string.IsNullOrWhiteSpace(privateKey))
    {
        Console.Error.WriteLine($"{KeyVariable} isn't set, so the feeds can't be signed.");
        return 1;
    }
    if (string.IsNullOrEmpty(UpdateFeed.PublicKey))
    {
        Console.Error.WriteLine("UpdateFeed.PublicKey is empty. Add the public key to the app before releasing with updates.");
        return 1;
    }
    if (UpdateFeed.PublicKeyFor(privateKey) != UpdateFeed.PublicKey)
    {
        Console.Error.WriteLine($"{KeyVariable} isn't the private half of UpdateFeed.PublicKey, so copies couldn't install this update.");
        return 1;
    }

    var notes = Changes(File.ReadAllText(notesPath));
    var published = DateTimeOffset.UtcNow;
    foreach (var runtime in Runtimes)
    {
        var name = UpdateFeed.ZipName(version, runtime);
        var zip = Path.Combine(folder, name);
        var data = File.ReadAllBytes(zip);
        var download = new Uri($"https://github.com/{repository}/releases/download/v{version}/{name}");
        var feed = UpdateFeed.WriteFeed(version, runtime, notes, download, data.Length, UpdateFeed.Sign(privateKey, data), published);
        var feedBytes = new UTF8Encoding(false).GetBytes(feed);
        var feedPath = Path.Combine(folder, UpdateFeed.FileName(runtime));
        File.WriteAllBytes(feedPath, feedBytes);
        File.WriteAllText(feedPath + ".signature", UpdateFeed.Sign(privateKey, feedBytes), Encoding.ASCII);
        Console.WriteLine($"Wrote {Path.GetFileName(feedPath)} for {name}");
    }
    return 0;
}

// The release notes' list of changes, shown in the update window.
static string Changes(string notes)
{
    var lines = notes.Replace("\r\n", "\n").Split('\n');
    var start = Array.FindIndex(lines, l => l.Trim() == "## Changes");
    var body = start < 0 ? lines : lines.Skip(start + 1).TakeWhile(l => !l.StartsWith("## ", StringComparison.Ordinal));
    return string.Join("\n", body).Trim();
}
