using PdcHomeConnect.Core.Hub;

namespace PdcHomeConnect.Tests;

public class PairingTests
{
    private static (PairingManager Manager, ManualTimeProvider Time, TokenStore Store) Create()
    {
        var time = new ManualTimeProvider();
        var store = new TokenStore(null, Log.For<TokenStore>(), time);
        return (new PairingManager(store, Log.For<PairingManager>(), time), time, store);
    }

    [Fact]
    public void Pairing_with_correct_code_issues_valid_token()
    {
        var (manager, _, store) = Create();
        PairingCode? shown = null;
        manager.CodeIssued += c => shown = c;

        var code = manager.RequestCode("HA test", "10.0.0.2");
        Assert.NotNull(shown);
        Assert.Equal(6, code.Code.Length);

        var outcome = manager.TryPair(code.Display, "HA test", "10.0.0.2", out var token);
        Assert.Equal(PairOutcome.Success, outcome);
        Assert.False(string.IsNullOrEmpty(token));
        Assert.Single(store.Clients);
        Assert.NotNull(manager.ValidateToken(token!, "10.0.0.2"));
        Assert.Null(manager.ValidateToken(token + "x", "10.0.0.2"));
        Assert.Null(manager.Current);
    }

    [Fact]
    public void Repeated_request_within_a_minute_reuses_code()
    {
        var (manager, time, _) = Create();
        var issued = 0;
        manager.CodeIssued += _ => issued++;
        var first = manager.RequestCode("a", null);
        time.Advance(TimeSpan.FromSeconds(30));
        var second = manager.RequestCode("a", null);
        Assert.Equal(first.Code, second.Code);
        Assert.Equal(1, issued);

        time.Advance(TimeSpan.FromSeconds(45));
        var third = manager.RequestCode("a", null);
        Assert.Equal(2, issued);
        Assert.NotNull(third);
    }

    [Fact]
    public void Wrong_codes_lock_out_after_max_attempts()
    {
        var (manager, _, _) = Create();
        var code = manager.RequestCode("a", null);
        var wrong = code.Code == "000000" ? "111111" : "000000";
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(PairOutcome.InvalidCode, manager.TryPair(wrong, "a", null, out _));
        }

        Assert.Equal(PairOutcome.TooManyAttempts, manager.TryPair(wrong, "a", null, out _));
        Assert.Equal(PairOutcome.NoActiveCode, manager.TryPair(code.Code, "a", null, out _));
    }

    [Fact]
    public void Codes_expire()
    {
        var (manager, time, _) = Create();
        var code = manager.RequestCode("a", null);
        time.Advance(PairingManager.CodeLifetime + TimeSpan.FromSeconds(1));
        Assert.Equal(PairOutcome.NoActiveCode, manager.TryPair(code.Code, "a", null, out _));
    }

    [Fact]
    public void Token_store_persists_hashes_only()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pdc-test-{Guid.NewGuid():N}.json");
        try
        {
            var store = new TokenStore(path, Log.For<TokenStore>());
            store.Add("HA", "secret-token", "1.2.3.4");
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("secret-token", text);
            var reloaded = new TokenStore(path, Log.For<TokenStore>());
            Assert.NotNull(reloaded.Validate("secret-token", null));
            Assert.True(reloaded.RemoveByToken("secret-token"));
            Assert.Null(new TokenStore(path, Log.For<TokenStore>()).Validate("secret-token", null));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
