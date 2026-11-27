using System.Text.Json;
using TranslationFoundation;

namespace SimpleTranslatorCS;

internal static class ServerProviderCatalog
{
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    internal static List<ServerProviderConfig.AiProfile> AiProfiles(ServerProviderConfig config, bool admin)
    {
        var result = config.AiProfiles.Select(Copy).ToList();
        if (config.EnableBuiltinProviders)
            foreach (var preset in TranslationProviderPresets.Ai.Where(p => admin || !p.AdminOnly))
            {
                var settings = Copy(config.Ai);
                settings.Provider = preset.Name; settings.Endpoint = preset.Endpoint; settings.Model = preset.Model;
                settings.AllowedModels = []; settings.Adapter = "auto";
                result.Add(new() { Id = "builtin:" + preset.Id, Name = preset.Name, Settings = settings });
            }
        return result;
    }
    internal static List<ServerProviderConfig.MachineProfile> MachineProfiles(ServerProviderConfig config, bool admin)
    {
        var result = config.MachineProfiles.Select(Copy).ToList();
        if (config.EnableBuiltinProviders)
            foreach (var preset in TranslationProviderPresets.Machine.Where(p => admin || !p.AdminOnly))
            {
                var settings = Copy(config.Machine);
                settings.Provider = preset.Name; settings.Endpoint = preset.Endpoint; settings.TargetLanguage = preset.TargetLanguage; settings.Region = "";
                result.Add(new() { Id = "builtin:" + preset.Id, Name = preset.Name, Settings = settings });
            }
        return result;
    }
    internal static void Select(ServerProviderConfig config, TranslationJobRequest request, bool admin)
    {
        if (request.ProfileId == "default") return;
        if (request.Mode == "ai") config.Ai = AiProfiles(config, admin).FirstOrDefault(p => p.Id == request.ProfileId)?.Settings
            ?? throw new InvalidOperationException("供应商预设不可用，请重新校验配置；本机和自定义接口需管理员权限。");
        else config.Machine = MachineProfiles(config, admin).FirstOrDefault(p => p.Id == request.ProfileId)?.Settings
            ?? throw new InvalidOperationException("机器翻译预设不可用，请重新校验配置。");
    }
}
