namespace VirtoCommerce.Sanity.Core.Models;

/// <summary>
/// One dataset of a <see cref="SanityProject"/> entry.
/// </summary>
public class SanityDataset
{
    /// <summary>
    /// Dataset name. Required; entries without it are skipped.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Document types fetched and indexed from this dataset.
    /// When empty, the store-level DocumentTypes setting is used.
    /// </summary>
    public string[] DocumentTypes { get; set; } = [];

    /// <summary>
    /// Marks the dataset as the priority one: it is queried first, so its documents win
    /// when the same document exists in several datasets of the project. Conflicts are always logged.
    /// </summary>
    public bool IsPriority { get; set; }
}
