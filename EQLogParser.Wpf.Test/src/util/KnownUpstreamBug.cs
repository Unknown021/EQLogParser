namespace EQLogParser.Wpf.Test
{
  /// <summary>
  /// Wraps assertions for tests that currently fail due to a bug in upstream
  /// kauffman12/EQLogParser's own code (not something this fork owns or can fix
  /// directly). Swallows the failure so the fork's sync/release pipeline isn't
  /// blocked on a bug that isn't ours - but if the assertion starts passing (upstream
  /// fixed it), this deliberately fails the test so the fix doesn't go unnoticed:
  /// remove the Track() wrapper once that happens.
  /// </summary>
  internal static class KnownUpstreamBug
  {
    internal static void Track(string upstreamNote, Action assertion)
    {
      try
      {
        assertion();
      }
      catch (AssertFailedException)
      {
        return;
      }

      Assert.Fail($"This test was wrapped in KnownUpstreamBug.Track, but it just passed - the fix likely landed upstream. Remove the wrapper. Tracking note: {upstreamNote}");
    }
  }
}
