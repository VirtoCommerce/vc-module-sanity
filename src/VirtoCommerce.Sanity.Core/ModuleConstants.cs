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
                yield return General.PageType;
            }
        }
    }
}
