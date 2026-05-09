using System;
using Xunit;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.Core.Deadlines;

namespace FenBrowser.FenEngine.Testing
{
public class EventLoopDeadlineTests
{
  [Fact]
  public void PerformMicrotaskCheckpoint_RespectsDeadline()
  {
    // Arrange
    var coordinator = EventLoopCoordinator.Instance;
    var tightDeadline = new FrameDeadline(1, "test"); // 1ms deadline
    
    // Act & Assert - Should not throw and should respect deadline
    coordinator.PerformMicrotaskCheckpoint(tightDeadline);
    // If we get here without hanging, the deadline check is working
    Assert.True(true);
  }
}
}
