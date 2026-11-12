# Copy this file to scripts\backup.settings.psd1 (untracked, gitignored) and adjust.
# Command-line parameters of backup-mods.ps1 / verify-backup.ps1 override these values.
@{
    # Where backup-mods.ps1 writes backup-<timestamp> folders.
    # Must be outside the source, outside this repo and outside the game folder.
    DestinationRoot = 'D:\PummelBackups'

    # Roots that verify-backup.ps1 checks by default (primary first, then extra copies).
    BackupRoots     = @('D:\PummelBackups', 'X:\second-copy\PummelBackups')

    # Optional: game install folder, used only for the "destination inside game folder" safety check.
    # If omitted (and no -GameRoot is given) that single check is skipped with a warning.
    GameRoot        = 'C:\Program Files (x86)\Steam\steamapps\common\Pummel Party'
}
