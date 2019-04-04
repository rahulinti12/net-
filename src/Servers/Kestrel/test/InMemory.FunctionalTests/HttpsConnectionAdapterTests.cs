// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https.Internal;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.Server.Kestrel.InMemory.FunctionalTests.TestTransport;
using Microsoft.AspNetCore.Testing;
using Microsoft.AspNetCore.Testing.xunit;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace Microsoft.AspNetCore.Server.Kestrel.InMemory.FunctionalTests
{
    public class HttpsConnectionAdapterTests : LoggedTest
    {
        private static X509Certificate2 _x509Certificate2 = TestResources.GetTestCertificate();
        private static X509Certificate2 _x509Certificate2NoExt = TestResources.GetTestCertificate("no_extensions.pfx");

        [Fact]
        public async Task CanReadAndWriteWithHttpsConnectionAdapter()
        {
            var listenOptions = new ListenOptions(new IPEndPoint(IPAddress.Loopback, 0))
            {
                ConnectionAdapters =
                {
                    new HttpsConnectionAdapter(new HttpsConnectionAdapterOptions { ServerCertificate = _x509Certificate2 })
                }
            };

            using (var server = new TestServer(App, new TestServiceContext(LoggerFactory), listenOptions))
            {
                var result = await server.HttpClientSlim.PostAsync($"https://localhost:{server.Port}/",
                    new FormUrlEncodedContent(new[] {
                        new KeyValuePair<string, string>("content", "Hello World?")
                    }),
                    validateCertificate: false);

                Assert.Equal("content=Hello+World%3F", result);
                await server.StopAsync();
            }
        }

        [Fact]
        public async Task HandshakeDetailsAreAvailable()
        {
            var listenOptions = new ListenOptions(new IPEndPoint(IPAddress.Loopback, 0))
            {
                ConnectionAdapters =
                {
                    new HttpsConnectionAdapter(new HttpsConnectionAdapterOptions { ServerCertificate = _x509Certificate2 })
                }
            };

            using (var server = new TestServer(context =>
            {
                var tlsFeature = context.Features.Get<ITlsHandshakeFeature>();
                Assert.NotNull(tlsFeature);
                Assert.True(tlsFeature.Protocol > SslProtocols.None, "Protocol");
                Assert.True(tlsFeature.CipherAlgorithm > CipherAlgorithmType.Null, "Cipher");
                Assert.True(tlsFeature.CipherStrength > 0, "CipherStrength");
                Assert.True(tlsFeature.HashAlgorithm >= HashAlgorithmType.None, "HashAlgorithm"); // May be None on Linux.
                Assert.True(tlsFeature.HashStrength >= 0, "HashStrength"); // May be 0 for some algorithms
                Assert.True(tlsFeature.KeyExchangeAlgorithm > ExchangeAlgorithmType.None, "KeyExchangeAlgorithm");
                Assert.True(tlsFeature.KeyExchangeStrength >= 0, "KeyExchangeStrength"); // May be 0 on mac

                return context.Response.WriteAsync("hello world");
            }, new TestServiceContext(LoggerFactory), listenOptions))
            {
                var result = await server.HttpClientSlim.GetStringAsync($"https://localhost:{server.Port}/", validateCertificate: false);
                Assert.Equal("hello world", result);
                await server.StopAsync();
            }
        }

        [Fact]
        public async Task RequireCertificateFailsWhenNoCertificate()
        {
            var listenOptions = new ListenOptions(new IPEndPoint(IPAddress.Loopback, 0))
            {
                ConnectionAdapters =
                {
                    new HttpsConnectionAdapter(new HttpsConnectionAdapterOptions
                    {
                        ServerCertificate = _x509Certificate2,
                        ClientCertificateMode = ClientCertificateMode.RequireCertificate
                    })
                }
            };


            using (var server = new TestServer(App, new TestServiceContext(LoggerFactory), listenOptions))
            {
                await Assert.ThrowsAnyAsync<Exception>(
                    () => server.HttpClientSlim.GetStringAsync($"https://localhost:{server.Port}/"));
                await server.StopAsync();
            }
        }

        [Fact]
        public async Task AllowCertificateContinuesWhenNoCertificate()
        {
            var listenOptions = new ListenOptions(new IPEndPoint(IPAddress.Loopback, 0))
            {
                ConnectionAdapters =
                {
                    new HttpsConnectionAdapter(new HttpsConnectionAdapterOptions
                    {
                        ServerCertificate = _x509Certificate2,
                        ClientCertificateMode = ClientCertificateMode.AllowCertificate
                    })
                }
            };

            using (var server = new TestServer(context =>
                {
                    var tlsFeature = context.Features.Get<ITlsConnectionFeature>();
                    Assert.NotNull(tlsFeature);
                    Assert.Null(tlsFeature.ClientCertificate);
                    return context.Response.WriteAsync("hello world");
                }, new TestServiceContext(LoggerFactory), listenOptions))
            {
                var result = await server.HttpClientSlim.GetStringAsync($"https://localhost:{server.Port}/", validateCertificate: false);
                Assert.Equal("hello world", result);
                await server.StopAsync();
            }
        }

        [Fact]
        public void ThrowsWhenNoServerCertificateIsProvided()
        {
            Assert.Throws<ArgumentException>(() => new HttpsConnectionAdapter(
                new HttpsConnectionAdapterOptions())
                );
        }

        [Fact]
        public async Task UsesProvidedServerCertificate()
        {
            var listenOptions = new ListenOptions(new IPEndPoint(IPAddress.Loopback, 0))
            {
                ConnectionAdapters =
                {
                    new HttpsConnectionAdapter(new HttpsConnectionAdapterOptions { ServerCertificate = _x509Certificate2 })
                }
            };

            using (var server = new TestServer(context => Task.CompletedTask, new TestServiceContext(LoggerFactory), listenOptions))
            {
                using (var connection = server.CreateConnection())
                {
                    // SslStream is used to ensure the certificate is actually passed to the server
                    // HttpClient might not send the certificate because it is invalid or it doesn't match any
                    // of the certificate authorities sent by the server in the SSL handshake.
                    var stream = OpenSslStream(connection.Stream);
                    await stream.AuthenticateAsClientAsync("localhost", new X509CertificateCollection(), SslProtocols.Tls12 | SslProtocols.Tls11, false);
                    Assert.True(stream.RemoteCertificate.Equals(_x509Certificate2));
                }
                await server.StopAsync();
            }
        }

        [Fact]
        public async Task UsesProvidedServerCertificateSelector()
        {
            var selectorCalled = 0;
            var listenOptions = new ListenOptions(new IPEndPoint(IPAddress.Loopback, 0))
            {
                ConnectionAdapters =
                {
                    new HttpsConnectionAdapter(new HttpsConnectionAdapterOptions
                    {
                        ServerCertificateSelector = (connection, name) =>
                        {
                            Assert.NotNull(connection);
                            Assert.NotNull(connection.Features.Get<SslStream>());
                            Assert.Equal("localhost", name);
                            selectorCalled++;
                            return _x509Certificate2;
                        }
                    })
                }
            };
            using (var server = new TestServer(context => Task.CompletedTask, new TestServiceContext(LoggerFactory), listenOptions))
            {
                using (var connection = server.CreateConnection())
                {
                    // SslStream is used to ensure the certificate is actually passed to the server
                    // HttpClient might not send the certificate because it is invalid or it doesn't match any
                    // of the certificate authorities sent by the server in the SSL handshake.
                    var stream = OpenSslStream(connection.Stream);
                    await stream.AuthenticateAsClientAsync("localhost", new X509CertificateCollection(), SslProtocols.Tls12 | SslProtocols.Tls11, false);
                    Assert.True(stream.RemoteCertificate.Equals(_x509Certificate2));
                    Assert.Equal(1, selectorCalled);
                }
                await server.StopAsync();
            }
        }

        [Fact]
        public async Task UsesProvidedServerCertificateSelectorEachTime()
        {
            var selectorCalled = 0;
            var listenOptions = new ListenOptions(new IPEndPoint(IPAddress.Loopback, 0))
            {
                ConnectionAdapters =
                {
                    new HttpsConnectionAdapter(new HttpsConnectionAdapterOptions
                    {
                        ServerCertificateSelector = (connection, name) =>
                        {
                            Assert.NotNull(connection);
                            Assert.NotNull(connection.Features.Get<SslStream>());
                            Assert.Equal("localhost", name);
                            selectorCalled++;
                            if (selectorCalled == 1)
                            {
                                return _x509Certificate2;
                            }
                            return _x509Certificate2NoExt;
                        }
                    })
                }
            };
            using (var server = new TestServer(context => Task.CompletedTask, new TestServiceContext(LoggerFactory), listenOptions))
            {
                using (var connection = server.CreateConnection())
                {
                    // SslStream is used to ensure the certificate is actually passed to the server
                    // HttpClient might not send the certificate because it is invalid or it doesn't match any
                    // of the certificate authorities sent by the server in the SSL handshake.
                    var stream = OpenSslStream(connection.Stream);
                    await stream.AuthenticateAsClientAsync("localhost", new X509CertificateCollection(), SslProtocols.Tls12 | SslProtocols.Tls11, false);
                    Assert.True(stream.RemoteCertificate.Equals(_x509Certificate2));
                    Assert.Equal(1, selectorCalled);
                }
                using (var connection = server.CreateConnection())
                {
                    // SslStream is used to ensure the certificate is actually passed to the server
                    // HttpClient might not send the certificate because it is invalid or it doesn't match any
                    // of the certificate authorities sent by the server in the SSL handshake.
                    var stream = OpenSslStream(connection.Stream);
                    await stream.AuthenticateAsClientAsync("localhost", new X509CertificateCollection(), SslProtocols.Tls12 | SslProtocols.Tls11, false);
                    Assert.True(stream.RemoteCertificate.Equals(_x509Certificate2NoExt));
                    Assert.Equal(2, selectorCalled);
                }
                await server.StopAsync();
            }
        }

        [Fact]
        public async Task UsesProvidedServerCertificateSelectorValidatesEkus()
        {
            var selectorCalled = 0;
            var listenOptions = new ListenOptions(new IPEndPoint(IPAddress.Loopback, 0))
            {
                ConnectionAdapters =
                {
                    new HttpsConnectionAdapter(new HttpsConnectionAdapterOptions
                    {
                        ServerCertificateSelector = (features, name) =>
                        {
                            selectorCalled++;
                            return TestResources.GetTestCertificate("eku.code_signing.pfx");
                        }
                    })
                }
            };
            using (var server = new TestServer(context => Task.CompletedTask, new TestServiceContext(LoggerFactory), listenOptions))
            {
                using (var connection = server.CreateConnection())
                {
                    // SslStream is used to ensure the certificate is actually passed to the server
                    // HttpClient might not send the certificate because it is invalid or it doesn't match any
                    // of the certificate authorities sent by the server in the SSL handshake.
                    var stream = OpenSslStream(connection.Stream);
                    await Assert.ThrowsAsync<IOException>(() =>
                        stream.AuthenticateAsClientAsync("localhost", new X509CertificateCollection(), SslProtocols.Tls12 | SslProtocols.Tls11, false));
                    Assert.Equal(1, selectorCalled);
                }
                await server.StopAsync();
            }
        }

        [Fact]
        public async Task UsesProvidedServerCertificateSelectorOverridesServerCertificate()
        {
            var selectorCalled = 0;
            var listenOptions = new ListenOptions(new IPEndPoint(IPAddress.Loopback, 0))
            {
                ConnectionAdapters =
                {
                    new HttpsConnectionAdapter(new HttpsConnectionAdapterOptions
                    {
                        ServerCertificate = _x509Certificate2NoExt,
                        ServerCertificateSelector = (connection, name) =>
                        {
                            Assert.NotNull(connection);
                            Assert.NotNull(connection.Features.Get<SslStream>());
                            Assert.Equal("localhost", name);
                            selectorCalled++;
                            return _x509Certificate2;
                        }
                    })
                }
            };
            using (var server = new TestServer(context => Task.CompletedTask, new TestServiceContext(LoggerFactory), listenOptions))
            {
                using (var connection = server.CreateConnection())
                {
                    // SslStream is used to ensure the certificate is actually passed to the server
                    // HttpClient might not send the certificate because it is invalid or it doesn't match any
                    // of the certificate authorities sent by the server in the SSL handshake.
                    var stream = OpenSslStream(connection.Stream);
                    await stream.AuthenticateAsClientAsync("localhost", new X509CertificateCollection(), SslProtocols.Tls12 | SslProtocols.Tls11, false);
                    Assert.True(stream.RemoteCertificate.Equals(_x509Certificate2));
                    Assert.Equal(1, selectorCalled);
                }
                await server.StopAsync();
            }
        }

        [Fact]
        public async Task UsesProvidedServerCertificateSelectorFailsIfYouReturnNull()
        {
            var selectorCalled = 0;
            var listenOptions = new ListenOptions(new IPEndPoint(IPAddress.Loopback, 0))
            {
                ConnectionAdapters =
                {
                    new HttpsConnectionAdapter(new HttpsConnectionAdapterOptions
                    {
                        ServerCertificateSelector = (features, name) =>
                        {
                            selectorCalled++;
                            return null;
                        }
                    })
                }
            };
            using (var server = new TestServer(context => Task.CompletedTask, new TestServiceContext(LoggerFactory), listenOptions))
            {
                using (var connection = server.CreateConnection())
                {
                    // SslStream is used to ensure the certificate is actually passed to the server
                    // HttpClient might not send the certificate because it is invalid or it doesn't match any
                    // of the certificate authorities sent by the server in the SSL handshake.
                    var stream = OpenSslStream(connection.Stream);
                    await Assert.ThrowsAsync<IOException>(() =>
                        stream.AuthenticateAsClientAsync("localhost", new X509CertificateCollection(), SslProtocols.Tls12 | SslProtocols.Tls11, false));
                    Assert.Equal(1, selectorCalled);
                }
                await server.StopAsync();
            }
        }

        [Theory]
        [InlineData(HttpProtocols.Http1)]
        [InlineData(HttpProtocols.Http1AndHttp2)] // Make sure Http/1.1 doesn't regress with Http/2 enabled.
        public async Task CertificatePassedToHttpContext(HttpProtocols httpProtocols)
        {
            var listenOptions = new ListenOptions(new IPEndPoint(IPAddress.Loopback, 0))
            {
                Protocols = httpProtocols,
                ConnectionAdapters =
                {
                    new HttpsConnectionAdapter(new HttpsConnectionAdapterOptions
                    {
                        ServerCertificate = _x509Certificate2,
                        ClientCertificateMode = ClientCertificateMode.RequireCertificate,
                        ClientCertificateValidation = (certificate, chain, sslPolicyErrors) => true
                    })
                }
            };

            using (var server = new TestServer(context =>
                {
                    var tlsFeature = context.Features.Get<ITlsConnectionFeature>();
                    Assert.NotNull(tlsFeature);
                    Assert.NotNull(tlsFeature.ClientCertificate);
                    Assert.NotNull(context.Connection.ClientCertificate);
                    return context.Response.WriteAsync("hello world");
                }, new TestServiceContext(LoggerFactory), listenOptions))
            {
                using (var connection = server.CreateConnection())
                {
                    // SslStream is used to ensure the certificate is actually passed to the server
                    // HttpClient might not send the certificate because it is invalid or it doesn't match any
                    // of the certificate authorities sent by the server in the SSL handshake.
                    var stream = OpenSslStream(connection.Stream);
                    await stream.AuthenticateAsClientAsync("localhost", new X509CertificateCollection(), SslProtocols.Tls12 | SslProtocols.Tls11, false);
                    await AssertConnectionResult(stream, true);
                }
                await server.StopAsync();
            }
        }

        [Fact]
        public async Task HttpsSchemePassedToRequestFeature()
        {
            var listenOptions = new ListenOptions(new IPEndPoint(IPAddress.Loopback, 0))
            {
                ConnectionAdapters =
                {
                    new HttpsConnectionAdapter(new HttpsConnectionAdapterOptions { ServerCertificate = _x509Certificate2 })
                }
            };

            using (var server = new TestServer(context => context.Response.WriteAsync(context.Request.Scheme), new TestServiceContext(LoggerFactory), listenOptions))
            {
                var result = await server.HttpClientSlim.GetStringAsync($"https://localhost:{server.Port}/", validateCertificate: false);
                Assert.Equal("https", result);
                await server.StopAsync();
            }
        }

        [Fact]
        public async Task DoesNotSupportTls10()
        {
            var listenOptions = new ListenOptions(new IPEndPoint(IPAddress.Loopback, 0))
            {
                ConnectionAdapters =
                {
                    new HttpsConnectionAdapter(new HttpsConnectionAdapterOptions
                    {
                        ServerCertificate = _x509Certificate2,
                        ClientCertificateMode = ClientCertificateMode.RequireCertificate,
                        ClientCertificateValidation = (certificate, chain, sslPolicyErrors) => true
                    })
                }
            };

            using (var server = new TestServer(context => context.Response.WriteAsync("hello world"), new TestServiceContext(LoggerFactory), listenOptions))
            {
                // SslStream is used to ensure the certificate is actually passed to the server
                // HttpClient might not send the certificate because it is invalid or it doesn't match any
                // of the certificate authorities sent by the server in the SSL handshake.
                using (var connection = server.CreateConnection())
                {
                    var stream = OpenSslStream(connection.Stream);
                    var ex = await Assert.ThrowsAsync<IOException>(
                        async () => await stream.AuthenticateAsClientAsync("localhost", new X509CertificateCollection(), SslProtocols.Tls, false));
                }
                await server.StopAsync();
            }
        }

        [Theory]
        [Flaky("https://github.com/aspnet/AspNetCore/issues/7265", FlakyOn.All)]
        [InlineData(ClientCertificateMode.AllowCertificate)]
        [InlineData(ClientCertificateMode.RequireCertificate)]
        public async Task ClientCertificateValidationGetsCalledWithNotNullParameters(ClientCertificateMode mode)
        {
            var clientCertificateValidationCalled = false;
            var listenOptions = new ListenOptions(new IPEndPoint(IPAddress.Loopback, 0))
            {
                ConnectionAdapters =
                {
                    new HttpsConnectionAdapter(new HttpsConnectionAdapterOptions
                    {
                        ServerCertificate = _x509Certificate2,
                        ClientCertificateMode = mode,
                        ClientCertificateValidation = (certificate, chain, sslPolicyErrors) =>
                        {
                            clientCertificateValidationCalled = true;
                            Assert.NotNull(certificate);
                            Assert.NotNull(chain);
                            return true;
                        }
                    })
                }
            };

            using (var server = new TestServer(context => Task.CompletedTask, new TestServiceContext(LoggerFactory), listenOptions))
            {
                using (var connection = server.CreateConnection())
                {
                    var stream = OpenSslStream(connection.Stream);
                    await stream.AuthenticateAsClientAsync("localhost", new X509CertificateCollection(), SslProtocols.Tls12 | SslProtocols.Tls11, false);
                    await AssertConnectionResult(stream, true);
                    Assert.True(clientCertificateValidationCalled);
                }
                await server.StopAsync();
            }
        }

        [Theory]
        [SkipOnHelix]
        [InlineData(ClientCertificateMode.AllowCertificate)]
        [InlineData(ClientCertificateMode.RequireCertificate)]
        public async Task ValidationFailureRejectsConnection(ClientCertificateMode mode)
        {
            var listenOptions = new ListenOptions(new IPEndPoint(IPAddress.Loopback, 0))
            {
                ConnectionAdapters =
                {
                    new HttpsConnectionAdapter(new HttpsConnectionAdapterOptions
                    {
                        ServerCertificate = _x509Certificate2,
                        ClientCertificateMode = mode,
                        ClientCertificateValidation = (certificate, chain, sslPolicyErrors) => false
                    })
                }
            };

            using (var server = new TestServer(context => Task.CompletedTask, new TestServiceContext(LoggerFactory), listenOptions))
            {
                using (var connection = server.CreateConnection())
                {
                    var stream = OpenSslStream(connection.Stream);
                    await stream.AuthenticateAsClientAsync("localhost", new X509CertificateCollection(), SslProtocols.Tls12 | SslProtocols.Tls11, false);
                    await AssertConnectionResult(stream, false);
                }
                await server.StopAsync();
            }
        }

        [Theory]
        [InlineData(ClientCertificateMode.AllowCertificate)]
        [InlineData(ClientCertificateMode.RequireCertificate)]
        public async Task RejectsConnectionOnSslPolicyErrorsWhenNoValidation(ClientCertificateMode mode)
        {
            var listenOptions = new ListenOptions(new IPEndPoint(IPAddress.Loopback, 0))
            {
                ConnectionAdapters =
                {
                    new HttpsConnectionAdapter(new HttpsConnectionAdapterOptions
                    {
                        ServerCertificate = _x509Certificate2,
                        ClientCertificateMode = mode
                    })
                }
            };

            using (var server = new TestServer(context => Task.CompletedTask, new TestServiceContext(LoggerFactory), listenOptions))
            {
                using (var connection = server.CreateConnection())
                {
                    var stream = OpenSslStream(connection.Stream);
                    await stream.AuthenticateAsClientAsync("localhost", new X509CertificateCollection(), SslProtocols.Tls12 | SslProtocols.Tls11, false);
                    await AssertConnectionResult(stream, false);
                }
                await server.StopAsync();
            }
        }

        [Fact]
        public async Task CertificatePassedToHttpContextIsNotDisposed()
        {
            var listenOptions = new ListenOptions(new IPEndPoint(IPAddress.Loopback, 0))
            {
                ConnectionAdapters =
                {
                    new HttpsConnectionAdapter(new HttpsConnectionAdapterOptions
                    {
                        ServerCertificate = _x509Certificate2,
                        ClientCertificateMode = ClientCertificateMode.RequireCertificate,
                        ClientCertificateValidation = (certificate, chain, sslPolicyErrors) => true
                    })
                }
            };

            RequestDelegate app = context =>
            {
                var tlsFeature = context.Features.Get<ITlsConnectionFeature>();
                Assert.NotNull(tlsFeature);
                Assert.NotNull(tlsFeature.ClientCertificate);
                Assert.NotNull(context.Connection.ClientCertificate);
                Assert.NotNull(context.Connection.ClientCertificate.PublicKey);
                return context.Response.WriteAsync("hello world");
            };

            using (var server = new TestServer(app, new TestServiceContext(LoggerFactory), listenOptions))
            {
                // SslStream is used to ensure the certificate is actually passed to the server
                // HttpClient might not send the certificate because it is invalid or it doesn't match any
                // of the certificate authorities sent by the server in the SSL handshake.
                using (var connection = server.CreateConnection())
                {
                    var stream = OpenSslStream(connection.Stream);
                    await stream.AuthenticateAsClientAsync("localhost", new X509CertificateCollection(), SslProtocols.Tls12 | SslProtocols.Tls11, false);
                    await AssertConnectionResult(stream, true);
                }
                await server.StopAsync();
            }
        }

        [Theory]
        [InlineData("no_extensions.pfx")]
        public void AcceptsCertificateWithoutExtensions(string testCertName)
        {
            var certPath = TestResources.GetCertPath(testCertName);
            TestOutputHelper.WriteLine("Loading " + certPath);
            var cert = new X509Certificate2(certPath, "testPassword");
            Assert.Empty(cert.Extensions.OfType<X509EnhancedKeyUsageExtension>());

            new HttpsConnectionAdapter(new HttpsConnectionAdapterOptions
            {
                ServerCertificate = cert,
            });
        }

        [Theory]
        [InlineData("eku.server.pfx")]
        [InlineData("eku.multiple_usages.pfx")]
        public void ValidatesEnhancedKeyUsageOnCertificate(string testCertName)
        {
            var certPath = TestResources.GetCertPath(testCertName);
            TestOutputHelper.WriteLine("Loading " + certPath);
            var cert = new X509Certificate2(certPath, "testPassword");
            Assert.NotEmpty(cert.Extensions);
            var eku = Assert.Single(cert.Extensions.OfType<X509EnhancedKeyUsageExtension>());
            Assert.NotEmpty(eku.EnhancedKeyUsages);

            new HttpsConnectionAdapter(new HttpsConnectionAdapterOptions
            {
                ServerCertificate = cert,
            });
        }

        [Theory]
        [InlineData("eku.code_signing.pfx")]
        [InlineData("eku.client.pfx")]
        public void ThrowsForCertificatesMissingServerEku(string testCertName)
        {
            var certPath = TestResources.GetCertPath(testCertName);
            TestOutputHelper.WriteLine("Loading " + certPath);
            var cert = new X509Certificate2(certPath, "testPassword");
            Assert.NotEmpty(cert.Extensions);
            var eku = Assert.Single(cert.Extensions.OfType<X509EnhancedKeyUsageExtension>());
            Assert.NotEmpty(eku.EnhancedKeyUsages);

            var ex = Assert.Throws<InvalidOperationException>(() =>
                new HttpsConnectionAdapter(new HttpsConnectionAdapterOptions
                {
                    ServerCertificate = cert,
                }));

            Assert.Equal(CoreStrings.FormatInvalidServerCertificateEku(cert.Thumbprint), ex.Message);
        }

        private static async Task App(HttpContext httpContext)
        {
            var request = httpContext.Request;
            var response = httpContext.Response;
            while (true)
            {
                var buffer = new byte[8192];
                var count = await request.Body.ReadAsync(buffer, 0, buffer.Length);
                if (count == 0)
                {
                    break;
                }
                await response.Body.WriteAsync(buffer, 0, count);
            }
        }

        private static SslStream OpenSslStream(Stream rawStream, X509Certificate2 clientCertificate = null)
        {
            return new SslStream(rawStream, false, (sender, certificate, chain, errors) => true,
                (sender, host, certificates, certificate, issuers) => clientCertificate ?? _x509Certificate2);
        }

        private static async Task AssertConnectionResult(SslStream stream, bool success)
        {
            var request = Encoding.UTF8.GetBytes("GET / HTTP/1.0\r\n\r\n");
            await stream.WriteAsync(request, 0, request.Length);
            var reader = new StreamReader(stream);
            string line = null;
            if (success)
            {
                line = await reader.ReadLineAsync();
                Assert.Equal("HTTP/1.1 200 OK", line);
            }
            else
            {
                try
                {
                    line = await reader.ReadLineAsync();
                }
                catch (IOException) { }
                Assert.Null(line);
            }
        }
    }
}
