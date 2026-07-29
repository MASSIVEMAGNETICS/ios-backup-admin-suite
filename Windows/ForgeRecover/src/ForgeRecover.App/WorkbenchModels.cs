using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using ForgeRecover.Core;

namespace ForgeRecover.App;

public sealed class ArtifactSelectionRow : INotifyPropertyChanged
{
    private bool _isSelected;

    public ArtifactSelectionRow(ArtifactRecord artifact)
    {
        Artifact = artifact;
    }

    public ArtifactRecord Artifact { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public string Timestamp => Artifact.TimestampUtc?.ToString("u", CultureInfo.InvariantCulture) ?? string.Empty;
    public string Kind => Artifact.Kind.ToString();
    public string Primary => Artifact.Primary ?? string.Empty;
    public string Secondary => Artifact.Secondary ?? string.Empty;
    public string Direction => Artifact.Direction ?? string.Empty;
    public string Body => Artifact.Body ?? string.Empty;
    public string Source => Artifact.SourceRowId is null
        ? Artifact.SourceDatabase
        : $"{Artifact.SourceDatabase}#{Artifact.SourceRowId}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
