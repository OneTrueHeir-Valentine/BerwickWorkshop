using Berwick.Core;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Berwick.Workshop;

public partial class MainWindow
{
    private Ps2MemoryCardInfo? savesPrimary;
    private Ps2MemoryCardInfo? savesComparison;
    private string? savesPrimaryName;
    private string? savesComparisonName;

    private sealed record SaveFileRow(BerwickSaveFileSummary File, string Comparison)
    {
        public string Directory => File.Directory;
        public string Name => File.Name;
        public string Kind => File.Name.StartsWith("savedata.", StringComparison.Ordinal)
            ? "Normal slot" : File.Name == "suspend.000" ? "Suspend" : "Index";
        public string Occupancy => File.HasNonZeroBytes
            ? "Contains data; validity unknown" : "Zero-filled";
        public string Bytes => File.Length.ToString("N0");
    }

    private void ShowSaves(object sender, RoutedEventArgs e)
    {
        view = View.Saves;
        UpdatePage();
    }

    private void SavesOpenClicked(object sender, RoutedEventArgs e) => _ = OpenSavesCardAsync(false);
    private void SavesCompareClicked(object sender, RoutedEventArgs e) => _ = OpenSavesCardAsync(true);

    private async Task OpenSavesCardAsync(bool compare)
    {
        if (compare && savesPrimary is null) return;
        var picker = new OpenFileDialog
        {
            Title = compare ? "Compare a copied PS2 memory card (read-only)" : "Open a COPY of a PS2 memory card (read-only)",
            Filter = "PS2 file memory cards (*.ps2)|*.ps2|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(this) != true) return;

        var sourceFile = picker.FileName;
        SavesOpenButton.IsEnabled = false;
        SavesCompareButton.IsEnabled = false;
        SavesStatus.Text = "Inspecting a copy in read-only mode...";
        try
        {
            // The core opens the source with FileMode.Open / FileAccess.Read and has no writer.
            var inspected = await Task.Run(() => Ps2MemoryCardInspector.Inspect(sourceFile));
            if (compare)
            {
                savesComparison = inspected;
                savesComparisonName = Path.GetFileName(sourceFile);
            }
            else
            {
                savesPrimary = inspected;
                savesPrimaryName = Path.GetFileName(sourceFile);
                savesComparison = null;
                savesComparisonName = null;
            }
            PopulateSavesPage();
            SavesStatus.Text = "Card inspected. No files changed.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidDataException or ArgumentException or NotSupportedException)
        {
            // A rejected card never displaces the previously inspected card.
            SavesStatus.Text = ex is InvalidDataException && ex.Message.Contains("unformatted", StringComparison.OrdinalIgnoreCase)
                ? "This card image is erased or unformatted; no save files to list."
                : "Unable to inspect this card: " + ex.Message;
        }
        finally
        {
            SavesOpenButton.IsEnabled = true;
            SavesCompareButton.IsEnabled = savesPrimary is not null;
        }
    }

    private void PopulateSavesPage()
    {
        if (savesPrimary is null) return;
        var info = savesPrimary;
        SavesCardSummary.Text = $"{savesPrimaryName}: PS2 card format {info.Version}, " +
            $"{info.ClustersPerCard:N0} clusters, " +
            $"{(info.HasSpareBytes ? "page spare/ECC bytes present" : "no page spare/ECC bytes")}, " +
            $"{info.Entries.Count:N0} directory entries.";
        SavesComparisonSummary.Text = savesComparison is null
            ? "No comparison card selected."
            : $"Comparing against {savesComparisonName}. Only file metadata and fingerprints are compared; gameplay state is not decoded.";

        var other = savesComparison?.BerwickFiles.ToDictionary(
            item => (item.Directory, item.Name));
        var rows = info.BerwickFiles
            .OrderBy(f => f.Directory, StringComparer.Ordinal)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .Select(file =>
            {
                var status = "Not compared";
                if (other is not null)
                    status = !other.TryGetValue((file.Directory, file.Name), out var match)
                        ? "Missing in comparison"
                        : file.Length == match.Length && file.Sha256 == match.Sha256 ? "Same" : "Different";
                return new SaveFileRow(file, status);
            }).ToList();
        if (other is not null)
        {
            var current = rows.Select(r => (r.Directory, r.Name)).ToHashSet();
            rows.AddRange(savesComparison!.BerwickFiles
                .Where(file => !current.Contains((file.Directory, file.Name)))
                .Select(file => new SaveFileRow(file, "Only in comparison")));
        }
        SavesGrid.ItemsSource = rows;
        SavesCount.Text = $"{rows.Count} Berwick file entries; a nonzero file is not proof that a save is loadable.";
        SavesGrid.SelectedItem = rows.FirstOrDefault(r => r.Kind == "Normal slot" &&
            r.File.HasNonZeroBytes) ?? rows.FirstOrDefault();
        if (rows.Count == 0) SavesSelected.Text = "No recognized Berwick save files on this formatted card.";
    }

    private void SavesSelectedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SavesSelected is null) return;
        if (SavesGrid.SelectedItem is not SaveFileRow row)
        {
            SavesSelected.Text = "Select a save, suspend or index file.";
            return;
        }
        SavesSelected.Text = $"{row.Directory}/{row.Name}\n" +
            $"Type: {row.Kind}\n" +
            $"File length: {row.File.Length:N0} bytes\n" +
            $"Data occupancy: {row.Occupancy}\n" +
            $"Comparison: {row.Comparison}\n\n" +
            "Read-only metadata only. The save's chapter, characters, story flags, " +
            "integrity and actual in-game load validity are not decoded. " +
            "This view never edits memory cards or changes the Workshop project.";
    }

    private void SavesGoLive(object sender, RoutedEventArgs e)
    {
        if (primary?.Supported != true)
        {
            SavesStatus.Text = "Open a supported game disc before using Live Game.";
            return;
        }
        view = View.Live;
        UpdatePage();
    }
}
