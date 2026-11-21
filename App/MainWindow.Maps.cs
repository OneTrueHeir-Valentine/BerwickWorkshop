using Berwick.Core;

namespace Berwick.Workshop;

public partial class MainWindow
{
    private async Task OpenMaps()
    {
        if (primary is not { Supported: true } disc) return;
        await OpenEvents();
        if (primary != disc || events is not { } library) return;
        await MapsPage.OpenAsync(disc, session, library, (number, bytes) => CheckEventFits(library, number, bytes), ScriptFitProblem);
    }

    private void BeginMapUnitEdit(MapUnitEditRequest request)
    {
        var fields = ArmyData.Fields;
        OpenEditor(new(View.Maps, request.Key, "Unit", request.Host, request.Fields, request.Button,
        [
            new("Unit record", EditSession.ArmyUnits, fields.Where(f => f.Group.Length == 0).ToList()),
            new("Weapon levels", EditSession.ArmyUnits, fields.Where(f => f.Group == "Weapon levels").ToList()),
            new("Personal skills", EditSession.ArmyUnits, fields.Where(f => f.Group == "Personal skills").ToList()),
            new("Items", EditSession.ArmyUnits, fields.Where(f => f.Group == "Items").ToList()),
            new("Dropped when defeated", EditSession.ArmyUnits, fields.Where(f => f.Group == "Drops").ToList()),
        ], "This unit's changes apply wherever this army is used. A class change affects only this unit; its stats become the new class's base plus its own bonus."));
    }
}
