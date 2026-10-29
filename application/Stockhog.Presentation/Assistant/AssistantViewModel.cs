using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Assistant;
using Stockhog.Presentation.Localization;
using Stockhog.Presentation.Settings;
using Stockhog.Presentation.Shell;
using Stockhog.Presentation.Trading;
using Microsoft.Extensions.Logging;

namespace Stockhog.Presentation.Assistant;

/// <summary>
/// Chat with the AI assistant about backtest results, prices and the account. Answers stream in as they are
/// written. The assistant can fill in the order ticket (as <see cref="IAssistantActions"/>), never submit it.
/// </summary>
public sealed partial class AssistantViewModel : PageViewModel, IAssistantActions, IRecipient<AskAssistantMessage>
{
    private readonly IAssistantClient _client;
    private readonly IDispatcher _dispatcher;
    private readonly IMessenger _messenger;
    private readonly SynchronizationContext? _ui;
    private IAssistantConversation? _conversation;
    private CancellationTokenSource? _answering;

    public AssistantViewModel(IAssistantClient client, IDispatcher dispatcher, IMessenger messenger, StatusViewModel status, ILogger<AssistantViewModel> logger)
        : base(status, logger)
    {
        _client = client;
        _dispatcher = dispatcher;
        _messenger = messenger;
        _ui = SynchronizationContext.Current;
        messenger.RegisterAll(this);
    }

    public ObservableCollection<ChatMessage> Messages { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _input = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand), nameof(NewChatCommand))]
    private bool _isAnswering;

    [ObservableProperty] private bool _isConfigured;
    [ObservableProperty] private string? _model;

    /// <summary>What the assistant is doing right now, e.g. reading the ranking.</summary>
    [ObservableProperty] private string? _activity;

    protected override Task LoadAsync() => RefreshStatusAsync();

    /// <summary>Called when the page is shown again, e.g. after a key was entered in Settings.</summary>
    public async Task RefreshStatusAsync()
    {
        var status = await _dispatcher.SendAsync(new GetAssistantStatusQuery());
        if (status.IsSuccess)
            (IsConfigured, Model) = (status.Value.IsConfigured, status.Value.Model);
    }

    public async void Receive(AskAssistantMessage message)
    {
        await RefreshStatusAsync();
        if (IsAnswering)
            return;

        Input = message.Question;
        if (CanSend())
            await SendAsync();
    }

    private bool CanSend() => !IsAnswering && !string.IsNullOrWhiteSpace(Input);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        await RefreshStatusAsync();
        if (!IsConfigured)
        {
            Status.Show(Loc.Get("Assistant_NoKey"));
            return;
        }

        var question = Input.Trim();
        Input = string.Empty;
        Messages.Add(ChatMessage.FromUser(question));
        var answer = ChatMessage.FromAssistant();
        Messages.Add(answer);

        _conversation ??= _client.StartConversation();
        _answering = new CancellationTokenSource();
        IsAnswering = true;

        try
        {
            await foreach (var update in _conversation.SendAsync(question, _answering.Token))
            {
                switch (update)
                {
                    case AssistantTextDelta delta:
                        answer.Append(delta.Text);
                        Activity = null;
                        break;
                    case AssistantToolUse tool:
                        Activity = Loc.TryGet($"Assistant_Tool_{tool.ToolName}", out var text) ? text : Loc.Format("Assistant_ToolGeneric", tool.ToolName);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            answer.Append(Loc.Get("Assistant_Stopped"));
        }
        catch (AssistantException ex)
        {
            answer.MarkFailed(ex.Message);
        }
        finally
        {
            answer.IsStreaming = false;
            Activity = null;
            IsAnswering = false;
            _answering.Dispose();
            _answering = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsAnswering))]
    private void Stop() => _answering?.Cancel();

    private bool CanStartNewChat() => !IsAnswering;

    /// <summary>Starts over: the assistant forgets the conversation.</summary>
    [RelayCommand(CanExecute = nameof(CanStartNewChat))]
    private void NewChat()
    {
        _conversation = null;
        Messages.Clear();
    }

    [RelayCommand]
    private void OpenTrading() => _messenger.Send(new NavigateMessage(typeof(TradingViewModel)));

    [RelayCommand]
    private void OpenSettings() => _messenger.Send(new NavigateMessage(typeof(SettingsViewModel)));

    /// <summary>Called by the assistant's tool on a worker thread; the ticket and the chat belong to the UI thread.</summary>
    public void ProposeOrder(OrderProposal proposal) => OnUi(() =>
    {
        _messenger.Send(new PrefillOrderTicketMessage(proposal));
        Messages.Add(ChatMessage.FromProposal(Loc.Format("Assistant_ProposalNote",
            Loc.Get($"OrderSide_{proposal.Side}"), proposal.Quantity, proposal.Symbol)));
    });

    private void OnUi(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
            action();
        else
            _ui.Post(_ => action(), null);
    }
}

/// <summary>One message in the chat. The assistant's text grows while it streams in.</summary>
public sealed partial class ChatMessage : ObservableObject
{
    private ChatMessage(ChatRole role, string text)
    {
        Role = role;
        _text = text;
    }

    public ChatRole Role { get; }

    public bool IsUser => Role == ChatRole.User;

    public bool IsProposal => Role == ChatRole.Proposal;

    [ObservableProperty] private string _text;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private bool _isError;

    public static ChatMessage FromUser(string text) => new(ChatRole.User, text);

    public static ChatMessage FromAssistant() => new(ChatRole.Assistant, string.Empty) { IsStreaming = true };

    public static ChatMessage FromProposal(string text) => new(ChatRole.Proposal, text);

    public void Append(string text) => Text += text;

    public void MarkFailed(string error)
    {
        Text = string.IsNullOrEmpty(Text) ? error : $"{Text}\n\n{error}";
        IsError = true;
    }
}

public enum ChatRole
{
    User,
    Assistant,
    Proposal
}
