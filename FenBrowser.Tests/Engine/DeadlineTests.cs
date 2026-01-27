using Xunit;
using FenBrowser.Core.Deadlines;
using FenBrowser.FenEngine.Layout.Algorithms;
using FenBrowser.FenEngine.Layout;
using System;
using System.Threading;

namespace FenBrowser.Tests.Engine
{
    public class DeadlineTests
    {
        [Fact]
        public void Deadline_Throws_When_Expired()
        {
            // 1ms budget
            var deadline = new FrameDeadline(0.1, "Test"); 
            Thread.Sleep(50); // Sleep 50ms (well over 1ms)
            
            Assert.True(deadline.IsExpired);
            Assert.Throws<DeadlineExceededException>(() => deadline.Check());
        }

        [Fact]
        public void Deadline_DoesNotThrow_When_Valid()
        {
            var deadline = new FrameDeadline(1000, "Test");
            Assert.False(deadline.IsExpired);
            deadline.Check(); // Should pass
        }
    }
}
