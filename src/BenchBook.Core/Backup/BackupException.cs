namespace BenchBook.Core.Backup;

/// <summary>A backup or restore that can't go ahead. The message is for the user.</summary>
public sealed class BackupException(string message, Exception? inner = null) : Exception(message, inner)
{
    public static BackupException NotABackup(Exception? inner = null) =>
        new("This isn't a BenchBook backup, or it's damaged.", inner);

    public static BackupException FormatOne() =>
        new("This backup was made by an older BenchBook for Mac, which only a Mac can read. On the Mac, update BenchBook and back up again, then restore that backup here.");

    public static BackupException NewerFormat() =>
        new("This backup was made by a newer version of BenchBook. Update BenchBook, then restore it.");

    public static BackupException Damaged(string path) =>
        new($"This backup is damaged: “{path}” doesn't match what was backed up. Your library hasn't been changed.");

    public static BackupException ArchiveFailed(Exception? inner = null) =>
        new("The backup file couldn't be written. Check there's enough space where you're saving it.", inner);

    public static BackupException MissingFile(string path) =>
        new($"The backup wasn't made because “{path}” is missing from your library. Your library hasn't been changed.");
}
