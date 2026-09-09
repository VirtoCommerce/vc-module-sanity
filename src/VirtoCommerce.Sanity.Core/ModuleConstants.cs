using System.Collections.Generic;
using VirtoCommerce.Platform.Core.Settings;

namespace VirtoCommerce.Sanity.Core;

public static class ModuleConstants
{
    private const string GroupName = "Sanity";

    public static class Security
    {
        public static class Permissions
        {
            public const string Access = "sanity:access";
            public const string Create = "sanity:create";
            public const string Read = "sanity:read";
            public const string Update = "sanity:update";
            public const string Delete = "sanity:delete";

            public static string[] AllPermissions { get; } =
            [
                Access,
                Create,
                Read,
                Update,
                Delete,
            ];
        }
    }

    public static class Settings
    {
        public static class General
        {
            public static SettingDescriptor Enabled { get; } = new()
            {
                Name = $"{GroupName}.Enabled",
                GroupName = $"CMS|{GroupName}",
                ValueType = SettingValueType.Boolean,
                IsPublic = true,
                DefaultValue = false,
            };

            public static SettingDescriptor ProjectId { get; } = new()
            {
                Name = $"{GroupName}.ProjectId",
                GroupName = $"CMS|{GroupName}",
                ValueType = SettingValueType.ShortText,
                IsPublic = true,
                DefaultValue = string.Empty,
            };

            public static SettingDescriptor Dataset { get; } = new()
            {
                Name = $"{GroupName}.Dataset",
                GroupName = $"CMS|{GroupName}",
                ValueType = SettingValueType.ShortText,
                IsPublic = true,
                DefaultValue = "production",
            };

            // Also serves as the default token for entries of the Projects setting that carry no "apiToken" of their own,
            // so a shared token does not have to sit in the Projects JSON.
            public static SettingDescriptor ApiToken { get; } = new()
            {
                Name = $"{GroupName}.ApiToken",
                GroupName = $"CMS|{GroupName}",
                ValueType = SettingValueType.SecureString,
                DefaultValue = string.Empty,
            };

            public static SettingDescriptor DocumentTypes { get; } = new()
            {
                Name = $"{GroupName}.DocumentTypes",
                GroupName = $"CMS|{GroupName}",
                ValueType = SettingValueType.ShortText,
                DefaultValue = "page",
            };

            // The single JSON setting describing all Sanity sources of the store: an array of projects, each with its own
            // credentials, datasets and document types, e.g.
            // [{"projectId": "abc", "apiToken": "sk...", "datasets": {"production": "page,footerNavigation"}, "priorityDataset": "production"}].
            // The "apiToken" entry field is optional and inherits the ApiToken setting. "priorityDataset" names the dataset
            // that wins when the same document exists in several datasets of the project; conflicts are always logged.
            // When empty, the module works with the single project from the ProjectId, Dataset and DocumentTypes settings.
            // Not public because entries may carry API tokens.
            public static SettingDescriptor Projects { get; } = new()
            {
                Name = $"{GroupName}.Projects",
                GroupName = $"CMS|{GroupName}",
                ValueType = SettingValueType.Json,
                DefaultValue = "[]",
            };

            // Legacy single-type setting, superseded by DocumentTypes. Kept registered so existing store values are still readable.
            public static SettingDescriptor PageType { get; } = new()
            {
                Name = $"{GroupName}.PageType",
                GroupName = $"CMS|{GroupName}",
                ValueType = SettingValueType.ShortText,
                DefaultValue = "page",
                IsHidden = true,
            };
        }

        public static IEnumerable<SettingDescriptor> AllSettings
        {
            get
            {
                yield return General.Enabled;
                yield return General.ProjectId;
                yield return General.Dataset;
                yield return General.ApiToken;
                yield return General.DocumentTypes;
                yield return General.Projects;
                yield return General.PageType;
            }
        }

        public static IEnumerable<SettingDescriptor> StoreLevelSettings
        {
            get
            {
                yield return General.Enabled;
                yield return General.ProjectId;
                yield return General.Dataset;
                yield return General.ApiToken;
                yield return General.DocumentTypes;
                yield return General.Projects;
                yield return General.PageType;
            }
        }
    }
}
