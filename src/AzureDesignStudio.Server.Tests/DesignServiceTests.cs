using System.Security.Claims;
using AzureDesignStudio.Server.Models;
using AzureDesignStudio.Server.Services;
using AzureDesignStudio.SharedModels.Protos;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AzureDesignStudio.Server.Tests;

public class DesignServiceTests
{
    private readonly DesignDbContext _db = TestData.NewDb();
    private readonly Guid _user = Guid.NewGuid();

    private DesignService Service => new(_db, NullLogger<DesignService>.Instance);

    private Task<SaveDesignResponse> Save(string name, string data = "{}", Guid? user = null) =>
        Service.Save(new SaveDesignRequest { Name = name, Data = data }, TestServerCallContext.ForUser(user ?? _user));

    // --- who is calling ---

    [Fact]
    public async Task CallerWithoutAnObjectIdIsRefused()
    {
        var response = await Service.Save(new SaveDesignRequest { Name = "a", Data = "{}" }, TestServerCallContext.WithoutIdentity());

        Assert.Equal(401, response.StatusCode);
        Assert.Empty(_db.AdsDesigns);
    }

    [Fact]
    public async Task ObjectIdThatIsNotAGuidIsRefusedInsteadOfFailingTheCall()
    {
        // Every RPC builds a Guid from the claim; this used to be an unhandled FormatException.
        var context = TestServerCallContext.WithClaims(new Claim("oid", "not-a-guid"));

        Assert.Equal(401, (await Service.Save(new SaveDesignRequest { Name = "a", Data = "{}" }, context)).StatusCode);
        Assert.Equal(401, (await Service.GetSaved(new Empty(), context)).StatusCode);
        Assert.Equal(401, (await Service.Load(new LoadDesignRequest { Name = "a" }, context)).StatusCode);
        Assert.Equal(401, (await Service.Delete(new DeleteDesignRequest { Name = "a" }, context)).StatusCode);
    }

    [Fact]
    public async Task ShortOidClaimIdentifiesTheCaller()
    {
        // What arrives when inbound claim mapping is switched off.
        var context = TestServerCallContext.WithClaims(new Claim("oid", _user.ToString()));

        var response = await Service.Save(new SaveDesignRequest { Name = "a", Data = "{}" }, context);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(_user, Assert.Single(_db.AdsDesigns).UserId);
    }

    // --- save ---

    [Fact]
    public async Task SavingAnExistingNameReplacesTheDesign()
    {
        await Save("net", """{"v":1}""");

        var response = await Save("net", """{"v":2}""");

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("""{"v":2}""", Assert.Single(_db.AdsDesigns).DesignData);
    }

    [Fact]
    public async Task QuotaStopsAnEleventhDesign()
    {
        for (var i = 0; i < 10; i++)
            Assert.Equal(200, (await Save($"design {i}")).StatusCode);

        Assert.Equal(507, (await Save("one more")).StatusCode);
        Assert.Equal(10, _db.AdsDesigns.Count());
    }

    [Fact]
    public async Task UserAtTheQuotaCanStillSaveChangesToAnExistingDesign()
    {
        // The quota was checked before the lookup, so at ten designs nothing could be saved.
        for (var i = 0; i < 10; i++)
            await Save($"design {i}");

        var response = await Save("design 3", """{"edited":true}""");

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("""{"edited":true}""", _db.AdsDesigns.Single(d => d.Name == "design 3").DesignData);
    }

    [Fact]
    public async Task QuotaIsPerUser()
    {
        for (var i = 0; i < 10; i++)
            await Save($"design {i}");

        Assert.Equal(200, (await Save("mine", user: Guid.NewGuid())).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad/name")]
    [InlineData("semi;colon")]
    public async Task InvalidNameIsRejected(string name)
    {
        Assert.Equal(400, (await Save(name)).StatusCode);
        Assert.Empty(_db.AdsDesigns);
    }

    // --- load, list, delete ---

    [Fact]
    public async Task LoadingAMissingDesignAnswersNotFound()
    {
        // The handler assigned the missing data to the response; protobuf setters reject
        // null, so this ended as an unhandled ArgumentNullException instead of a 404.
        var response = await Service.Load(new LoadDesignRequest { Name = "nothing here" }, TestServerCallContext.ForUser(_user));

        Assert.Equal(404, response.StatusCode);
        Assert.Equal(string.Empty, response.Data);
    }

    [Fact]
    public async Task DesignsAreVisibleToTheirOwnerOnly()
    {
        await Save("secret plan", """{"mine":true}""");
        var stranger = TestServerCallContext.ForUser(Guid.NewGuid());

        Assert.Equal(404, (await Service.Load(new LoadDesignRequest { Name = "secret plan" }, stranger)).StatusCode);
        Assert.Empty((await Service.GetSaved(new Empty(), stranger)).Names);
        Assert.Equal(404, (await Service.Delete(new DeleteDesignRequest { Name = "secret plan" }, stranger)).StatusCode);
        Assert.Single(_db.AdsDesigns);
    }

    [Fact]
    public async Task SavedDesignRoundTrips()
    {
        await Save("net", """{"nodes":[]}""");
        var me = TestServerCallContext.ForUser(_user);

        Assert.Equal(new[] { "net" }, (await Service.GetSaved(new Empty(), me)).Names);
        var loaded = await Service.Load(new LoadDesignRequest { Name = "net" }, me);
        Assert.Equal(200, loaded.StatusCode);
        Assert.Equal("""{"nodes":[]}""", loaded.Data);

        Assert.Equal(200, (await Service.Delete(new DeleteDesignRequest { Name = "net" }, me)).StatusCode);
        Assert.Empty(_db.AdsDesigns);
        Assert.Equal(404, (await Service.Delete(new DeleteDesignRequest { Name = "net" }, me)).StatusCode);
    }
}
