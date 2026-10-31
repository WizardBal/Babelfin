using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Babelfin.Engine.Tests.Settings;
using Jellyfin.Plugin.Babelfin.Configuration;
using Jellyfin.Plugin.Babelfin.Playback;
using Jellyfin.Plugin.Babelfin.Settings;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Babelfin.Engine.Tests.Regression;

[Collection("Preservation safety globals")]
public sealed class PlaybackLookupRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProgressWithExpectedTracksDoesNotQueryUserDatabaseEvenWhenSendingCorrection(bool correction)
    {
        using var host = new LocalStorageTestHost();
        host.CreatePlugin(new PluginConfiguration { ConfigVersion = PluginConfiguration.CurrentVersion });
        var sessions = CallProxy.Create<ISessionManager>();
        var users = CallProxy.Create<IUserManager>((_, _) => throw new InvalidOperationException("An established play must not fetch a user entity"));
        var registry = new PlayRegistry();
        var item = new Movie { Id = Guid.NewGuid(), Name = "Synthetic movie" };
        var userId = Guid.NewGuid();
        var session = new SessionInfo(sessions.Proxy, NullLogger.Instance)
        {
            Id = "synthetic-session", UserId = userId,
            PlayState = new PlayerStateInfo { AudioStreamIndex = correction ? 1 : 2, SubtitleStreamIndex = -1 },
            Capabilities = new ClientCapabilities { SupportedCommands = new[] { GeneralCommandType.SetAudioStreamIndex } },
        };
        registry.Update(session.Id, entry =>
        {
            entry.UserId = userId;
            entry.ItemId = item.Id;
            entry.Playing = true;
            entry.HasExpected = true;
            entry.ExpectedAudio = 2;
            entry.ExpectedSubtitle = -1;
            entry.ChoiceAudio = 2;
            entry.ChoiceSubtitle = -1;
        });
        var monitor = new PlaybackMonitor(sessions.Proxy, registry, null!, CallProxy.Create<IMediaSourceManager>().Proxy,
            users.Proxy, CallProxy.Create<ILibraryManager>().Proxy, new ListActivityLog(), NullLogger<PlaybackMonitor>.Instance);
        var report = new PlaybackProgressEventArgs { Session = session, Item = item };
        var handle = typeof(PlaybackMonitor).GetMethod("HandleReport", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (var i = 0; i < 30; i++) handle.Invoke(monitor, new object[] { report });
        Assert.DoesNotContain("GetUserById", users.Recorder.Calls);
        Assert.True(registry.Get(session.Id)!.HasReport);
        Assert.Equal(correction ? 1 : 0, sessions.Recorder.Calls.Count(name => name == "SendGeneralCommand"));
    }
}
