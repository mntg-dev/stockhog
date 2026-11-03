using System.ComponentModel.DataAnnotations;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Assistant;
using Stockhog.Application.Brokers;
using Stockhog.Presentation.Localization;
using Stockhog.Presentation.Shell;
using Microsoft.Extensions.Logging;

namespace Stockhog.Presentation.Settings;

/// <summary>
/// Language (applied immediately) and defaults for new sessions, plus read-only facts about the installation.
/// </summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly IUserSettingsStore _store;
    private readonly IDispatcher _dispatcher;
    private UserSettings _settings;

    public SettingsViewModel(
        IUserSettingsStore store,
        IDispatcher dispatcher,
        UserSettings settings,
        AppEnvironmentInfo environment,
        StatusViewModel status,
        ILogger<SettingsViewModel> logger)
        : base(status, logger)
    {
        _store = store;
        _dispatcher = dispatcher;
        _settings = settings;
        Environment = environment;
        _selectedCulture = Loc.SupportedCultures.FirstOrDefault(c => c.Name == settings.Culture) ?? Loc.Polish;
        _defaultSymbols = settings.DefaultSymbols;
        _defaultInitialCapital = settings.DefaultInitialCapital;
    }

    public AppEnvironmentInfo Environment { get; }

    public IReadOnlyList<CultureInfo> Cultures => Loc.SupportedCultures;

    [ObservableProperty] private CultureInfo _selectedCulture;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessage = " ")]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _defaultSymbols;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(typeof(decimal), "1", "1000000000000", ErrorMessage = " ")]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private decimal _defaultInitialCapital;

    partial void OnSelectedCultureChanged(CultureInfo value)
    {
        Loc.Instance.SetCulture(value);
        Persist(_settings with { Culture = value.Name });
    }

    private bool CanSave() => !HasErrors;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        // The symbol picker leaves a trailing ", " ready for the next symbol; don't store it.
        Persist(_settings with { DefaultSymbols = DefaultSymbols.Trim().TrimEnd(',', ';').Trim(), DefaultInitialCapital = DefaultInitialCapital });
        Status.Show(Loc.Get("Status_SettingsSaved"));
    }

    private void Persist(UserSettings settings)
    {
        _settings = settings;
        _store.Save(settings);
    }

    /// <summary>Whether the AI assistant has an API key, and its model.</summary>
    [ObservableProperty] private string? _assistantStatus;

    protected override async Task LoadAsync()
    {
        await ShowAssistantStatusAsync(_dispatcher.SendAsync(new GetAssistantStatusQuery()));
        await ShowBrokerKeyStatusAsync(_dispatcher.SendAsync(new GetBrokerKeyStatusQuery()));
    }

    /// <summary>Where the Alpaca keys in use come from, and whether a restart is needed for new ones.</summary>
    [ObservableProperty] private string? _brokerKeyStatus;

    [ObservableProperty] private bool _brokerKeyRestartNeeded;

    [ObservableProperty] private bool _hasStoredBrokerKeys;

    /// <summary>Tries the keys against market data and the paper account without storing them.</summary>
    public async Task TestBrokerKeysAsync(BrokerKeysInput input)
    {
        var check = await _dispatcher.SendAsync(new TestBrokerKeysCommand(input.ApiKey, input.ApiSecret, input.PaperApiKey, input.PaperApiSecret));
        if (check.IsFailure)
            Status.Show(check.Error);
        else if (check.Value.IsOk)
            Status.Show(Loc.Format("Status_BrokerKeysWork", check.Value.AccountNumber ?? "?"));
        else
            Status.Show(Loc.Format("Status_BrokerKeysFailed", string.Join(" ", new[]
            {
                check.Value.DataError is { } data ? Loc.Format("Settings_BrokerDataError", data) : null,
                check.Value.TradingError is { } trading ? Loc.Format("Settings_BrokerTradingError", trading) : null
            }.OfType<string>())));
    }

    /// <summary>Tests the keys, then stores them encrypted for this Windows user; they are used from the next start.</summary>
    public Task SaveBrokerKeysAsync(BrokerKeysInput input) =>
        ShowBrokerKeyStatusAsync(
            _dispatcher.SendAsync(new SaveBrokerKeysCommand(input.ApiKey, input.ApiSecret, input.PaperApiKey, input.PaperApiSecret)),
            "Status_BrokerKeysSaved");

    [RelayCommand(CanExecute = nameof(HasStoredBrokerKeys))]
    private Task RemoveBrokerKeysAsync() =>
        ShowBrokerKeyStatusAsync(_dispatcher.SendAsync(new RemoveBrokerKeysCommand()), "Status_BrokerKeysRemoved");

    private async Task ShowBrokerKeyStatusAsync(Task<Application.Abstractions.Results.Result<BrokerKeyStatus>> request, string? successMessage = null)
    {
        var result = await request;
        if (result.IsFailure)
        {
            Status.Show(result.Error);
            return;
        }

        var status = result.Value;
        BrokerKeyStatus = status.Source switch
        {
            BrokerKeySource.Settings => Loc.Format("Settings_BrokerKeysFromSettings", status.MaskedKey ?? ""),
            BrokerKeySource.Configuration => Loc.Format("Settings_BrokerKeysFromConfig", status.MaskedKey ?? ""),
            _ => Loc.Get("Settings_BrokerKeysNone")
        };
        BrokerKeyRestartNeeded = status.RestartNeeded;
        HasStoredBrokerKeys = status.HasStoredKeys;
        RemoveBrokerKeysCommand.NotifyCanExecuteChanged();
        if (successMessage is not null)
            Status.Show(Loc.Get(successMessage));
    }

    /// <summary>Stores the key encrypted for this Windows user; the view passes it from a password box and clears it.</summary>
    [RelayCommand]
    private Task SaveApiKeyAsync(string? apiKey) =>
        string.IsNullOrWhiteSpace(apiKey)
            ? Task.CompletedTask
            : ShowAssistantStatusAsync(_dispatcher.SendAsync(new SaveAssistantApiKeyCommand(apiKey)), "Status_ApiKeySaved");

    [RelayCommand]
    private Task RemoveApiKeyAsync() =>
        ShowAssistantStatusAsync(_dispatcher.SendAsync(new SaveAssistantApiKeyCommand(null)), "Status_ApiKeyRemoved");

    private async Task ShowAssistantStatusAsync(Task<Application.Abstractions.Results.Result<AssistantStatus>> request, string? successMessage = null)
    {
        var result = await request;
        if (result.IsFailure)
        {
            Status.Show(result.Error);
            return;
        }

        AssistantStatus = result.Value.IsConfigured
            ? Loc.Format("Settings_AssistantReady", result.Value.Model)
            : Loc.Get("Settings_AssistantNoKey");
        if (successMessage is not null)
            Status.Show(Loc.Get(successMessage));
    }
}

/// <summary>What the user typed on the Settings page; the secrets come from password boxes and are not kept.</summary>
public sealed record BrokerKeysInput(string ApiKey, string ApiSecret, string? PaperApiKey, string? PaperApiSecret);
