using Atelier.Core.Inspection;
using ParagliderToolbox.Framework.Commands;
using ParagliderToolbox.Framework.Model;
using ParagliderToolbox.Framework.Modules;
using ParagliderToolbox.Modules.Core;

namespace ParagliderToolbox.Tests;

/// <summary>A node with saved, ignored and computed properties, registered by <see cref="TestModule"/>.</summary>
[Inspectable]
public partial class SampleNode : ProjectNode
{
    private double _span = 11.5;

    public SampleNode() => Name = "Sample";

    public double Span { get => _span; set => SetProperty(ref _span, value); }

    public SampleKind Kind { get; set; } = SampleKind.Glider;

    public double[] Points { get; set; } = [1, 2, 3];

    public Guid? Reference { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Transient { get; set; } = "not saved";

    public double HalfSpan => Span / 2;
}

public enum SampleKind
{
    Glider,
    Harness,
}

public sealed class TestModule : IToolboxModule
{
    public string Name => "Test";

    public void Register(Toolbox toolbox) =>
        toolbox.NodeTypes.Register<SampleNode>("sample", "Sample", Atelier.Controls.MaterialIconKind.Science);
}

/// <summary>Dialogs with fixed answers that record what was asked.</summary>
public sealed class FakeDialogs : IShellDialogs
{
    public SaveChangesChoice SaveChangesAnswer { get; set; } = SaveChangesChoice.Discard;
    public bool ConfirmAnswer { get; set; } = true;
    public string? TextAnswer { get; set; }
    public string? OpenAnswer { get; set; }
    public string? SaveAnswer { get; set; }
    public string? FolderAnswer { get; set; }
    public List<string> Asked { get; } = [];
    public bool WindowClosed { get; private set; }

    public Task<SaveChangesChoice> AskSaveChangesAsync(string documentName) { Asked.Add("SaveChanges"); return Task.FromResult(SaveChangesAnswer); }
    public Task<string?> PickFileToOpenAsync(string? initialDirectory) { Asked.Add("Open"); return Task.FromResult(OpenAnswer); }
    public Task<string?> PickFileToSaveAsync(string suggestedName, string? initialDirectory) { Asked.Add($"Save:{suggestedName}"); return Task.FromResult(SaveAnswer); }
    public Task ShowErrorAsync(string title, string message) { Asked.Add($"Error:{title}"); return Task.CompletedTask; }
    public Task ShowMessageAsync(string title, string message) { Asked.Add($"Message:{title}"); return Task.CompletedTask; }
    public Task<string?> PickOpenFileAsync(string title, string filter, string? initialDirectory) { Asked.Add($"OpenFile:{title}"); return Task.FromResult(OpenAnswer); }
    public Task<string?> PickSaveFileAsync(string title, string filter, string suggestedName, string? initialDirectory) { Asked.Add($"SaveFile:{suggestedName}"); return Task.FromResult(SaveAnswer); }
    public Task<string?> PickFolderAsync(string title, string? initialDirectory) { Asked.Add($"Folder:{title}"); return Task.FromResult(FolderAnswer); }
    public Task<bool> ConfirmAsync(string title, string message, string confirmText) { Asked.Add($"Confirm:{title}"); return Task.FromResult(ConfirmAnswer); }
    public Task<string?> AskTextAsync(string title, string label, string text) { Asked.Add($"Text:{title}"); return Task.FromResult(TextAnswer); }
    public void ShowCommandPalette() => Asked.Add("Palette");
    public void ShowKeybindingEditor() => Asked.Add("Keybindings");
    public void ResetLayout() => Asked.Add("ResetLayout");
    public void CloseWindow() => WindowClosed = true;
}

public static class TestToolbox
{
    public static Toolbox Create(FakeDialogs? dialogs = null)
    {
        var toolbox = new Toolbox();
        toolbox.Initialize([new CoreModule(), new TestModule()]);
        toolbox.Dialogs = dialogs ?? new FakeDialogs();
        return toolbox;
    }

    public static NodeType Type<T>(this Toolbox toolbox) where T : ProjectNode =>
        toolbox.NodeTypes.Types.Single(t => t.ClrType == typeof(T));
}
