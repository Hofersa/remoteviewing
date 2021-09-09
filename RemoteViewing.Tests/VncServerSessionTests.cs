#region License
/*
RemoteViewing VNC Client/Server Library for .NET
Copyright (c) 2019, 2020 Quamotion bvba
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/
#endregion

using Moq;
using RemoteViewing.Vnc;
using RemoteViewing.Vnc.Server;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace RemoteViewing.Tests
{
    public class VncServerSessionTests
    {
        public static IEnumerable<object[]> NegotiateVersionBoth38TestData()
        {
            yield return new object[]
            {
                null,
                AuthenticationMethod.None,
            };

            yield return new object[]
            {
                new VncServerSessionOptions()
                {
                    AuthenticationMethod = AuthenticationMethod.Password,
                },
                AuthenticationMethod.Password,
            };

            yield return new object[]
            {
                new VncServerSessionOptions()
                {
                    AuthenticationMethod = AuthenticationMethod.None,
                },
                AuthenticationMethod.None,
            };
        }

        [Fact]
        public void FramebufferSendChangesTest()
        {
            var session = new VncServerSession();
            var framebufferSourceMock = new Mock<IVncFramebufferSource>();
            var framebuffer = new VncFramebuffer("My Framebuffer", width: 0x4f4, height: 0x3c1, pixelFormat: VncPixelFormat.RGB32);
            framebufferSourceMock
                .Setup(f => f.Capture())
                .Returns(
                () =>
                {
                    return framebuffer;
                });
            session.FramebufferUpdateRequest = new FramebufferUpdateRequest(false, new VncRectangle(0, 0, 100, 100));
            session.SetFramebufferSource(framebufferSourceMock.Object);
            session.FramebufferSendChanges();

            Assert.Equal(framebuffer, session.Framebuffer);

            framebufferSourceMock = new Mock<IVncFramebufferSource>();
            framebufferSourceMock
                .Setup(f => f.Capture())
                .Throws(new IOException());

            session.FramebufferUpdateRequest = new FramebufferUpdateRequest(false, new VncRectangle(0, 0, 100, 100));
            session.SetFramebufferSource(framebufferSourceMock.Object);

            // should not throw and exception.
            session.FramebufferSendChanges();

            Assert.Equal(framebuffer, session.Framebuffer);
        }

        [Theory]
        [MemberData(nameof(NegotiateVersionBoth38TestData))]
        public void NegotiateVersionBoth38Test(VncServerSessionOptions sessionOptions, AuthenticationMethod expectedAuthenticationMethod)
        {
            using (var stream = new TestStream())
            {
                // Mimick the client negotiating RFB 3.8
                VncStream clientStream = new VncStream(stream.Input);
                clientStream.SendString("RFB 003.008\n");
                stream.Input.Position = 0;

                VncServerSession session = new VncServerSession();
                session.Connect(stream, sessionOptions, startThread: false);

                Assert.True(session.NegotiateVersion(out AuthenticationMethod[] methods));

                Assert.Collection(
                    methods,
                    (m) => Assert.Equal(expectedAuthenticationMethod, m));

                stream.Output.Position = 0;

                Assert.Equal(Encoding.UTF8.GetBytes("RFB 003.008\n"), ((MemoryStream)stream.Output).ToArray());
            }
        }

        [Theory]
        [InlineData("RFB 003.003\n")]
        [InlineData("RFB 003.005\n")]
        [InlineData("RFB 003.007\n")]
        [InlineData("RFB 003.009\n")]
        public void NegotiateVersionNot38Test(string version)
        {
            using (var stream = new TestStream())
            {
                // Mimick the client negotiating RFB 3.8
                VncStream clientStream = new VncStream(stream.Input);
                clientStream.SendString(version);
                stream.Input.Position = 0;

                VncServerSession session = new VncServerSession();
                session.Connect(stream, null, startThread: false);

                Assert.True(session.NegotiateVersion(out AuthenticationMethod[] methods));
                Assert.Empty(methods);

                stream.Output.Position = 0;

                Assert.Equal(Encoding.UTF8.GetBytes("RFB 003.008\n"), ((MemoryStream)stream.Output).ToArray());
            }
        }

        [Fact]
        public void NegotiateSecurityNoSecurityTypesTest()
        {
            using (var stream = new TestStream())
            {
                var session = new VncServerSession();
                session.Connect(stream, null, startThread: false);

                Assert.False(session.NegotiateSecurity(Array.Empty<AuthenticationMethod>()));

                VncStream serverStream = new VncStream(stream.Output);
                stream.Output.Position = 0;

                // Server should have sent a zero-length array, and a message explaining
                // the disconnect reason
                Assert.Equal(0, serverStream.ReceiveByte());
                Assert.Equal("The server and client could not agree on any authentication method.", serverStream.ReceiveString());
            }
        }

        [Fact]
        public void NegotiateSecurityInvalidMethodTest()
        {
            using (var stream = new TestStream())
            {
                // Have the client send authentication method 'None', while we only accept 'Password'
                VncStream clientStream = new VncStream(stream.Input);
                clientStream.SendByte((byte)AuthenticationMethod.None);
                stream.Input.Position = 0;

                var session = new VncServerSession();
                session.Connect(stream, null, startThread: false);

                Assert.False(session.NegotiateSecurity(new AuthenticationMethod[] { AuthenticationMethod.Password }));

                VncStream serverStream = new VncStream(stream.Output);
                stream.Output.Position = 0;

                // Server will have offered 1 authentication method, and disconnected when the client
                // accepted an invalid authentication method.
                Assert.Equal(1, serverStream.ReceiveByte()); // 1 authentication method offered
                Assert.Equal((byte)AuthenticationMethod.Password, serverStream.ReceiveByte()); // The authentication method offered
                Assert.Equal(1u, serverStream.ReceiveUInt32BE()); // authentication failed
                Assert.Equal("Invalid authentication method.", serverStream.ReceiveString()); // error message
            }
        }

        [Fact]
        public void NegotiateSecuritySuccessTest()
        {
            using (var stream = new TestStream())
            {
                // Have the client send authentication method 'None', which is what the server expects
                VncStream clientStream = new VncStream(stream.Input);
                clientStream.SendByte((byte)AuthenticationMethod.None);
                stream.Input.Position = 0;

                var session = new VncServerSession();
                session.Connect(stream, null, startThread: false);

                Assert.True(session.NegotiateSecurity(new AuthenticationMethod[] { AuthenticationMethod.None }));

                VncStream serverStream = new VncStream(stream.Output);
                stream.Output.Position = 0;

                // Server will have offered 1 authentication method, and successfully authenticated
                // the client
                Assert.Equal(1, serverStream.ReceiveByte()); // 1 authentication method offered
                Assert.Equal((byte)AuthenticationMethod.None, serverStream.ReceiveByte()); // The authentication method offered
                Assert.Equal(0u, serverStream.ReceiveUInt32BE()); // authentication succeeded
            }
        }

        [Fact]
        public void NegotiateSecurityIncorrectPasswordTest()
        {
            using (var stream = new TestStream())
            {
                // Have the client send authentication method 'Password', which is what the server expects
                VncStream clientStream = new VncStream(stream.Input);
                clientStream.SendByte((byte)AuthenticationMethod.Password);
                clientStream.Send(new byte[16]); // An empty response
                stream.Input.Position = 0;

                var session = new VncServerSession();
                session.Connect(stream, null, startThread: false);

                Assert.False(session.NegotiateSecurity(new AuthenticationMethod[] { AuthenticationMethod.Password }));

                VncStream serverStream = new VncStream(stream.Output);
                stream.Output.Position = 0;

                // Server will have offered 1 authentication method, and failed to authenticate
                // the client
                Assert.Equal(1, serverStream.ReceiveByte()); // 1 authentication method offered
                Assert.Equal((byte)AuthenticationMethod.Password, serverStream.ReceiveByte()); // The authentication method offered
                Assert.NotEqual(0u, serverStream.ReceiveUInt32BE()); // Challenge, part 1
                Assert.NotEqual(0u, serverStream.ReceiveUInt32BE()); // Challenge, part 2
                Assert.NotEqual(0u, serverStream.ReceiveUInt32BE()); // Challenge, part 3
                Assert.NotEqual(0u, serverStream.ReceiveUInt32BE()); // Challenge, part 4
                Assert.Equal(1u, serverStream.ReceiveUInt32BE()); // Authentication failed
                Assert.Equal("Failed to authenticate", serverStream.ReceiveString()); // Error message

                Assert.Equal(stream.Output.Length, stream.Output.Position);
                Assert.Equal(stream.Input.Length, stream.Input.Position);
            }
        }

        /// <summary>
        /// Tests the <see cref="VncServerSession.Encoders"/> property.
        /// </summary>
        [Fact]
        public void DefaultEncodersTest()
        {
            var session = new VncServerSession();
            Assert.Collection(
                session.Encoders,
                (e) => Assert.IsType<TightEncoder>(e));
        }

        /// <summary>
        /// Tests the <see cref="VncServerSession.HandleSetEncodings"/> method.
        /// </summary>
        /// <param name="forceConnect">
        /// A value indicating whether to set <see cref="VncServerSession.IsConnected"/>.
        /// </param>
        /// <param name="expectedEncoding">
        /// The expected encoding.
        /// </param>
        [InlineData(false, VncEncoding.Raw)]
        [InlineData(true, VncEncoding.Raw)]
        [Theory]
        public void HandleSetEncodingsTest(bool forceConnect, VncEncoding expectedEncoding)
        {
            using (var stream = new TestStream())
            {
                // Have the client send a list of supported encodings, including
                // Raw and zlib
                VncStream clientStream = new VncStream(stream.Input);
                clientStream.SendByte(0);
                clientStream.SendUInt16BE(2); // 2 encodings are supported
                clientStream.SendUInt32BE((uint)VncEncoding.Raw);
                clientStream.SendUInt32BE((uint)VncEncoding.Zlib);
                stream.Input.Position = 0;

                var session = new VncServerSession();
                session.Connect(stream, null, startThread: false, forceConnected: forceConnect);

                session.HandleSetEncodings();

                // The server should not have written any output, but switched
                // to zlib encoding
                VncStream serverStream = new VncStream(stream.Output);
                Assert.Equal(0, serverStream.Stream.Length);
                stream.Output.Position = 0;

                Assert.Equal(expectedEncoding, session.Encoder.Encoding);
            }
        }

        /// <summary>
        /// Tests the <see cref="VncServerSession.Connect(Stream, VncServerSessionOptions)"/> method, when passed
        /// invalid arguments.
        /// </summary>
        [Fact]
        public void ConnectNullTest()
        {
            var session = new VncServerSession();
            Assert.Throws<ArgumentNullException>(() => session.Connect(null, null));
        }

        /// <summary>
        /// Tests the <see cref="VncServerSession.SendLocalClipboardChange(string)"/> method, when passed
        /// invalid arguments.
        /// </summary>
        [Fact]
        public void SendLocalClipboardChangeNullTest()
        {
            var session = new VncServerSession();
            Assert.Throws<ArgumentNullException>(() => session.SendLocalClipboardChange(null));
        }

        /// <summary>
        /// Tests the <see cref="VncServerSession.FramebufferManualInvalidate(VncRectangle[])"/> method, when passed
        /// invalid arguments.
        /// </summary>
        [Fact]
        public void FramebufferManualInvalidateNullTest()
        {
            var session = new VncServerSession();
            Assert.Throws<ArgumentNullException>(() => session.FramebufferManualInvalidate(null));
        }

        /// <summary>
        /// Tests the handling of a SetDesktopSize message.
        /// </summary>
        [Fact]
        public void SetDesktopSizeTest()
        {
            var framebuffer1 = new VncFramebuffer("test-1", 100, 200, VncPixelFormat.RGB32);
            var framebuffer2 = new VncFramebuffer("test-2", 200, 400, VncPixelFormat.RGB32);
            var framebuffer = framebuffer1;

            var framebufferSourceMock = new Mock<IVncFramebufferSource>(MockBehavior.Strict);
            framebufferSourceMock
                .Setup(m => m.SetDesktopSize(200, 300))
                .Returns(ExtendedDesktopSizeStatus.Success)
                .Callback(() => { framebuffer = framebuffer2; });

            framebufferSourceMock
                .Setup(m => m.Capture())
                .Returns(() => framebuffer);

            using (var stream = new TestStream())
            {
                VncStream clientStream = new VncStream(stream.Input);

                // Negotiating the dessktop size
                clientStream.SendByte(0); // share desktop setting

                // Send a SetDesktopSize request
                clientStream.SendByte((byte)VncMessageType.SetDesktopSize);
                clientStream.SendByte(0); // padding
                clientStream.SendUInt16BE(200); // width
                clientStream.SendUInt16BE(300); // height
                clientStream.SendByte(1); // number of screens
                clientStream.SendByte(0); // padding
                clientStream.SendUInt32BE(1); // screen id
                clientStream.SendUInt16BE(0); // x position
                clientStream.SendUInt16BE(0); // x position
                clientStream.SendUInt16BE(200); // width
                clientStream.SendUInt16BE(300); // height
                clientStream.SendUInt32BE(0); // flags

                stream.Input.Position = 0;

                var session = new VncServerSession();
                session.SetFramebufferSource(framebufferSourceMock.Object);
                session.Connect(stream, null, startThread: false);

                // Negotiate the desktop
                session.NegotiateDesktop();
                Assert.Equal(VncPixelFormat.RGB32, session.ClientPixelFormat);

                // Handle the SetDesktopSize request
                session.HandleMessage();

                VncStream serverStream = new VncStream(stream.Output);
                stream.Output.Position = 0;

                // Desktop negotiation result
                Assert.Equal(100, serverStream.ReceiveUInt16BE());
                Assert.Equal(200, serverStream.ReceiveUInt16BE());
                var format = serverStream.Receive(16);
                Assert.Equal("test-1", serverStream.ReceiveString());

                // SetDesktopSize result
                Assert.Equal(0, serverStream.ReceiveUInt16BE()); // Update rectangle request
                Assert.Equal(1, serverStream.ReceiveUInt16BE()); // 1 rectangle;

                Assert.Equal((ushort)ExtendedDesktopSizeReason.Client, serverStream.ReceiveUInt16BE()); // x
                Assert.Equal((ushort)ExtendedDesktopSizeStatus.Success, serverStream.ReceiveUInt16BE()); // y
                Assert.Equal(100, serverStream.ReceiveUInt16BE()); // width
                Assert.Equal(200, serverStream.ReceiveUInt16BE()); // height

                Assert.Equal((int)VncEncoding.ExtendedDesktopSize, (int)serverStream.ReceiveUInt32BE()); // encoding

                Assert.Equal(1u, serverStream.ReceiveByte()); // Number of screens
                serverStream.Receive(3); // Padding
                Assert.Equal(0u, serverStream.ReceiveUInt32BE()); // screen ID
                Assert.Equal(0u, serverStream.ReceiveUInt16BE()); // x-position
                Assert.Equal(0u, serverStream.ReceiveUInt16BE()); // y-position
                Assert.Equal(100u, serverStream.ReceiveUInt16BE()); // width
                Assert.Equal(200u, serverStream.ReceiveUInt16BE()); // height
                Assert.Equal(0u, serverStream.ReceiveUInt32BE()); // flags

                Assert.Equal(stream.Output.Length, stream.Output.Position);
                Assert.Equal(stream.Input.Length, stream.Input.Position);
            }
        }
    }
}
