using System.Collections.Concurrent;
using System.Globalization;
using Atelier.Core.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ParagliderToolbox.Paraglider;
using ParagliderToolbox.Paraglider.Design;

namespace ParagliderToolbox.Modules.Paraglider;

/// <summary>
/// The choices for a new paraglider: its name, wing class (<see cref="GliderPresets"/>) and mesh detail, with what each
/// choice means; <see cref="CreateNode"/> builds the paraglider. The new-paraglider dialog binds to it.
/// </summary>
public sealed partial class NewParagliderOptions : ObservableObject
{
    // Triangle counts by class and detail, measured once per session.
    private static readonly ConcurrentDictionary<(WingClass, MeshDetail), int> s_triangles = new();

    private WingClass _class = WingClass.EnBLow;
    private MeshDetail _detail = MeshDetail.High;
    private string _name = GliderPresets.Info(WingClass.EnBLow).Name;
    private bool _nameEdited;

    /// <summary>Gets the mesh details offered (Custom is set in the properties later).</summary>
    public static IReadOnlyList<MeshDetail> Details { get; } = [MeshDetail.LowPoly, MeshDetail.Medium, MeshDetail.High];

    /// <summary>Gets or sets the wing class.</summary>
    public WingClass Class
    {
        get => _class;
        set
        {
            // Pressing the selected choice again keeps it (and puts its toggle back).
            if (!SetProperty(ref _class, value)) { OnPropertyChanged(); return; }
            if (!_nameEdited) SetProperty(ref _name, Info(value).Name, nameof(Name));
            OnPropertyChanged(nameof(Description));
        }
    }

    /// <summary>Gets or sets the mesh detail.</summary>
    public MeshDetail Detail
    {
        get => _detail;
        set { if (!SetProperty(ref _detail, value)) OnPropertyChanged(); }
    }

    /// <summary>Gets or sets the name of the new paraglider (the class's name until edited).</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value ?? string.Empty)) _nameEdited = true;
        }
    }

    /// <summary>Gets the description of the chosen class.</summary>
    public string Description => Info(_class).Description;

    /// <summary>Gets the names and descriptions of a class.</summary>
    public static WingClassInfo Info(WingClass wingClass) => GliderPresets.Info(wingClass);

    /// <summary>Gets the key numbers of a class, e.g. "AR 4.9 · 40 cells · 3 rows".</summary>
    public static string Summary(WingClass wingClass)
    {
        var design = GliderPresets.Create(wingClass, MeshDetail.Custom);
        return string.Create(CultureInfo.CurrentCulture,
            $"AR {design.FlatAspectRatio:0.0#} · {design.CellCount} cells · {design.RowPositions.Length} rows");
    }

    /// <summary>Gets the name of a mesh detail.</summary>
    public static string DetailName(MeshDetail detail) => detail switch
    {
        MeshDetail.LowPoly => "Low poly",
        MeshDetail.Medium => "Medium",
        MeshDetail.High => "High",
        _ => "Custom",
    };

    /// <summary>Gets what a mesh detail is for.</summary>
    public static string DetailUse(MeshDetail detail) => detail switch
    {
        MeshDetail.LowPoly => "for games",
        MeshDetail.Medium => "real time",
        MeshDetail.High => "renders and close-ups",
        _ => "",
    };

    /// <summary>Gets the triangle count of the chosen class at <paramref name="detail"/>, e.g. "≈ 24k triangles", once measured.</summary>
    public string Triangles(MeshDetail detail) => s_triangles.TryGetValue((_class, detail), out int count)
        ? count < 1000
            ? string.Create(CultureInfo.CurrentCulture, $"{count} triangles")
            : string.Create(CultureInfo.CurrentCulture, $"≈ {count / 1000.0:0}k triangles")
        : "counting…";

    /// <summary>Measures the triangle counts of every class and detail in the background (once per session), raising changes as they come in.</summary>
    public void StartCounting()
    {
        if (s_triangles.Count == GliderPresets.Classes.Count * Details.Count) return;
        // The chosen class first, then the others; low detail first (fast).
        var order = GliderPresets.Classes.OrderBy(c => c == _class ? 0 : 1).SelectMany(c => Details.Select(d => (c, d))).ToList();
        _ = Task.Run(() =>
        {
            foreach (var key in order)
            {
                if (s_triangles.ContainsKey(key)) continue;
                var model = GliderGenerator.Generate(GliderPresets.Create(key.c, key.d), new GenerateOptions(-1));
                s_triangles[key] = model.TriangleCount;
                Dispatcher.Post(() => OnPropertyChanged(nameof(Triangles)));
            }
        });
    }

    /// <summary>Creates the paraglider.</summary>
    public ParagliderNode CreateNode() => new(GliderPresets.Create(_class, _detail))
    {
        Name = string.IsNullOrWhiteSpace(_name) ? Info(_class).Name : _name.Trim(),
    };
}
