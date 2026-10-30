using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ember.Models;

namespace Ember.ViewModels;

/// <summary>Network page: one card per connected adapter with live download/upload.</summary>
public partial class NetworkViewModel : ViewModelBase
{
    private readonly Action _back;
    private readonly Dictionary<string, AdapterCard> _cards = new();

    public NetworkViewModel(Action back) => _back = back;

    public ObservableCollection<AdapterCard> Adapters { get; } = new();

    [ObservableProperty] private string _summary = "Looking for network adapters…";

    [RelayCommand]
    private void Back() => _back();

    public void Update(HardwareSnapshot s)
    {
        for (int i = 0; i < s.Network.Count; i++)
        {
            var info = s.Network[i];
            if (!_cards.TryGetValue(info.Id, out var card))
            {
                card = new AdapterCard(info.Id);
                _cards[info.Id] = card;
                Adapters.Add(card);
            }
            card.Apply(info);

            var at = Adapters.IndexOf(card);
            if (at != i && i < Adapters.Count) Adapters.Move(at, i);
        }

        foreach (var gone in _cards.Keys.Where(k => s.Network.All(n => n.Id != k)).ToList())
        {
            Adapters.Remove(_cards[gone]);
            _cards.Remove(gone);
        }

        var primary = s.Network.FirstOrDefault(n => n.IsPrimary);
        Summary = s.Network.Count == 0 ? "No connected adapters"
            : primary is null ? $"{s.Network.Count} connected adapters · no internet gateway"
            : $"Online through {primary.Name} · {Format.NetRate(primary.DownBps)} down · {Format.NetRate(primary.UpBps)} up";
    }
}

public partial class AdapterCard : ObservableObject
{
    private readonly double[] _down = new double[60];
    private readonly double[] _up = new double[60];

    public AdapterCard(string id) => Id = id;

    public string Id { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _kind = "";
    [ObservableProperty] private string _icon = "";
    [ObservableProperty] private bool _isPrimary;
    [ObservableProperty] private bool _isVirtual;
    [ObservableProperty] private string _downText = "";
    [ObservableProperty] private string _upText = "";
    [ObservableProperty] private string _ipText = "";
    [ObservableProperty] private string _linkText = "";
    [ObservableProperty] private string _totalsText = "";
    [ObservableProperty] private double[] _downHistory = Array.Empty<double>();
    [ObservableProperty] private double[] _upHistory = Array.Empty<double>();
    [ObservableProperty] private double _max = 1;

    public void Apply(NetAdapterInfo n)
    {
        Name = n.Name;
        Description = n.Description;
        Kind = n.Kind;
        Icon = n.Kind switch { "Wi-Fi" => "", "Bluetooth" => "", _ => "" };
        IsPrimary = n.IsPrimary;
        IsVirtual = n.IsVirtual;
        DownText = Format.NetRate(n.DownBps);
        UpText = Format.NetRate(n.UpBps);
        IpText = n.Ipv4 ?? "No IPv4 address";
        LinkText = n.LinkBps > 0 ? $"Link {Format.LinkSpeed(n.LinkBps)}" : "";
        TotalsText = $"{Format.Bytes(n.TotalDown)} received · {Format.Bytes(n.TotalUp)} sent since startup";

        Array.Copy(_down, 1, _down, 0, _down.Length - 1);
        Array.Copy(_up, 1, _up, 0, _up.Length - 1);
        _down[^1] = n.DownBps;
        _up[^1] = n.UpBps;
        DownHistory = (double[])_down.Clone();
        UpHistory = (double[])_up.Clone();
        Max = Math.Max(Math.Max(_down.Max(), _up.Max()) * 1.15, 64 * 1024);
    }
}
