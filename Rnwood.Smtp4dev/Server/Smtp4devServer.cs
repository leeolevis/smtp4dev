﻿using Microsoft.Extensions.Options;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Prng;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.X509;
using Rnwood.Smtp4dev.DbModel;
using Rnwood.Smtp4dev.Hubs;
using Rnwood.SmtpServer;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Rnwood.Smtp4dev.Server
{
    public class Smtp4devServer : IMessagesRepository
    {
        public Smtp4devServer(Func<Smtp4devDbContext> dbContextFactory, IOptions<ServerOptions> serverOptions, MessagesHub messagesHub, SessionsHub sessionsHub)
        {
            this.dbContextFactory = dbContextFactory;
            System.Security.Cryptography.X509Certificates.X509Certificate cert = CreateSelfSignedCertificate();

            ServerOptions serverOptionsValue = serverOptions.Value;
            this.smtpServer = new DefaultServer(serverOptionsValue.AllowRemoteConnections, serverOptionsValue.Port,
                serverOptionsValue.TlsMode == TlsMode.ImplicitTls ? cert : null,
                serverOptionsValue.TlsMode == TlsMode.StartTls ? cert : null
            );
            this.smtpServer.MessageReceivedEventHandler += OnMessageReceived;
            this.smtpServer.SessionCompletedEventHandler += OnSessionCompleted;
            this.smtpServer.SessionStartedHandler += OnSessionStarted;

            this.messagesHub = messagesHub;
            this.sessionsHub = sessionsHub;

            this.serverOptions = serverOptions; ;
        }

        private static System.Security.Cryptography.X509Certificates.X509Certificate CreateSelfSignedCertificate()
        {
            CryptoApiRandomGenerator randomGenerator = new CryptoApiRandomGenerator();
            SecureRandom random = new SecureRandom(randomGenerator);

            X509V3CertificateGenerator certGenerator = new X509V3CertificateGenerator();
            certGenerator.SetSubjectDN(new X509Name("CN=" + IPGlobalProperties.GetIPGlobalProperties().HostName));
            certGenerator.SetIssuerDN(new X509Name("CN=" + IPGlobalProperties.GetIPGlobalProperties().HostName));

            GeneralNames subjectAltName = new GeneralNames(new GeneralName(GeneralName.DnsName, IPGlobalProperties.GetIPGlobalProperties().HostName + "." + IPGlobalProperties.GetIPGlobalProperties().DomainName));
            certGenerator.AddExtension(X509Extensions.SubjectAlternativeName, false, subjectAltName);

            BigInteger serialNumber = BigIntegers.CreateRandomInRange(BigInteger.One, BigInteger.ValueOf(Int64.MaxValue), random);
            certGenerator.SetSerialNumber(serialNumber);
            certGenerator.SetSignatureAlgorithm("SHA256WithRSA");

            certGenerator.SetNotBefore(DateTime.UtcNow.Date);
            certGenerator.SetNotAfter(DateTime.UtcNow.Date.AddYears(10));

            KeyGenerationParameters keyGenerationParameters = new KeyGenerationParameters(random, 2048);

            RsaKeyPairGenerator keyPairGenerator = new RsaKeyPairGenerator();
            keyPairGenerator.Init(keyGenerationParameters);
            AsymmetricCipherKeyPair keypair = keyPairGenerator.GenerateKeyPair();
            certGenerator.SetPublicKey(keypair.Public);
            X509Certificate certificate = certGenerator.Generate(keypair.Private, random);

            return new System.Security.Cryptography.X509Certificates.X509Certificate(certificate.GetEncoded());
        }

        private IOptions<ServerOptions> serverOptions;
        private readonly IDictionary<ISession, Guid> activeSessionsToDbId = new Dictionary<ISession, Guid>();

        private static async Task UpdateDbSession(ISession session, Session dbSession)
        {
            dbSession.StartDate = session.StartDate;
            dbSession.EndDate = session.EndDate;
            dbSession.ClientAddress = session.ClientAddress.ToString();
            dbSession.ClientName = session.ClientName;
            dbSession.NumberOfMessages = (await session.GetMessages()).Count;
            dbSession.Log = (await session.GetLog()).ReadToEnd();
            dbSession.SessionErrorType = session.SessionErrorType;
            dbSession.SessionError = session.SessionError?.ToString();
        }

        public IQueryable<DbModel.Message> GetMessages()
        {
            return dbContextFactory().Messages;
        }


        public Task MarkMessageRead(Guid id)
        {
            return QueueTask(() =>
            {
                Smtp4devDbContext dbContent = dbContextFactory();
                DbModel.Message message = dbContent.Messages.FindAsync(id).Result;

                if (message.IsUnread)
                {
                    message.IsUnread = false;
                    dbContent.SaveChanges();
                    messagesHub.OnMessagesChanged().Wait();
                }
            }, true);
        }

        private async Task OnSessionStarted(object sender, SessionEventArgs e)
        {
            Console.WriteLine($"Session started. Client address {e.Session.ClientAddress}.");
            await QueueTask(() =>
            {

                Smtp4devDbContext dbContent = dbContextFactory();

                Session dbSession = new Session();
                UpdateDbSession(e.Session, dbSession).Wait();
                dbContent.Sessions.Add(dbSession);
                dbContent.SaveChanges();

                activeSessionsToDbId[e.Session] = dbSession.Id;

            }, false).ConfigureAwait(false);
        }

        private async Task OnSessionCompleted(object sender, SessionEventArgs e)
        {
            int messageCount = (await e.Session.GetMessages()).Count;
            Console.WriteLine($"Session completed. Client address {e.Session.ClientAddress}. Number of messages {messageCount}.");
			

            await QueueTask(() =>
            {
                Smtp4devDbContext dbContent = dbContextFactory();

                Session dbSession = dbContent.Sessions.Find(activeSessionsToDbId[e.Session]);
                UpdateDbSession(e.Session, dbSession).Wait();
                dbContent.SaveChanges();

                Smtp4devServer.TrimSessions(dbContent, serverOptions.Value);
                dbContent.SaveChanges();

                activeSessionsToDbId.Remove(e.Session);

                sessionsHub.OnSessionsChanged().Wait();

            }, false).ConfigureAwait(false);
        }

        private Task QueueTask(Action action, bool priority)
        {
            TaskCompletionSource<object> tcs = new TaskCompletionSource<object>();

            Action wrapper = () =>
            {
                try
                {
                    action();
                    tcs.SetResult(null);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());

                    tcs.SetException(e);
                }

            };


            if (priority)
            {
                priorityProcessingQueue.Add(wrapper);
            }
            else
            {
                processingQueue.Add(wrapper);
            }

            return tcs.Task;
        }

        internal Task DeleteSession(Guid id)
        {
            return QueueTask(() =>
            {
                Smtp4devDbContext dbContext = dbContextFactory();

                Session session = dbContext.Sessions.FirstOrDefault(s => s.Id == id);

                if (session == null)
                {
                    dbContext.Sessions.Remove(session);
                    dbContext.SaveChanges();
                    sessionsHub.OnSessionsChanged().Wait();
                }
            }, true);
        }

        internal Task DeleteAllSessions()
        {
            return QueueTask(() =>
            {
                Smtp4devDbContext dbContext = dbContextFactory();
                dbContext.Sessions.RemoveRange(dbContext.Sessions.Where(s => s.EndDate.HasValue));
                dbContext.SaveChanges();
                sessionsHub.OnSessionsChanged().Wait();
            }, true);
        }

        private async Task OnMessageReceived(object sender, MessageEventArgs e)
        {
            using (Stream stream = e.Message.GetData().Result)
            {
                string to = string.Join(", ", e.Message.Recipients);
                Console.WriteLine($"Message received. Client address {e.Message.Session.ClientAddress}. From {e.Message.From}. To {to}.");
                Message message = new MessageConverter().ConvertAsync(stream, e.Message.From, to).Result;
                message.IsUnread = true;

                await QueueTask(() =>
                {
                    Console.WriteLine("Processing received message");
                    Smtp4devDbContext dbContext = dbContextFactory();

                    Session dbSession = dbContext.Sessions.Find(activeSessionsToDbId[e.Message.Session]);
                    message.Session = dbSession;
                    dbContext.Messages.Add(message);

                    dbContext.SaveChanges();

                    Smtp4devServer.TrimMessages(dbContext, serverOptions.Value);
                    dbContext.SaveChanges();
                    messagesHub.OnMessagesChanged().Wait();
                    Console.WriteLine("Processing received message DONE");

                }, false).ConfigureAwait(false);
            }
        }

        public Task DeleteMessage(Guid id)
        {
            return QueueTask(() =>
            {
                Smtp4devDbContext dbContext = dbContextFactory();
                dbContext.Messages.RemoveRange(dbContext.Messages.Where(m => m.Id == id));
                dbContext.SaveChanges();
                messagesHub.OnMessagesChanged().Wait();
            }, true);
        }


        public Task DeleteAllMessages()
        {
            return QueueTask(() =>
            {
                Smtp4devDbContext dbContext = dbContextFactory();
                dbContext.Messages.RemoveRange(dbContext.Messages);
                dbContext.SaveChanges();
                messagesHub.OnMessagesChanged().Wait();
            }, true);
        }



        private static void TrimMessages(Smtp4devDbContext dbContext, ServerOptions serverOptions)
        {
            dbContext.Messages.RemoveRange(dbContext.Messages.OrderByDescending(m => m.ReceivedDate).Skip(serverOptions.NumberOfMessagesToKeep));
        }

        private static void TrimSessions(Smtp4devDbContext dbContext, ServerOptions serverOptions)
        {
            dbContext.Sessions.RemoveRange(dbContext.Sessions.Where(s => s.EndDate.HasValue).OrderByDescending(m => m.EndDate).Skip(serverOptions.NumberOfSessionsToKeep));
        }

        private Task ProcessingTaskWork()
        {
            while (!processingQueue.IsCompleted && !priorityProcessingQueue.IsCompleted)
            {
                Action nextItem;
                try
                {
                    BlockingCollection<Action>.TakeFromAny(new[] { priorityProcessingQueue, processingQueue }, out nextItem);
                }
                catch (InvalidOperationException)
                {
                    if (processingQueue.IsCompleted || priorityProcessingQueue.IsCompleted)
                    {
                        break;
                    }

                    throw;
                }

                nextItem();
            }

            return Task.CompletedTask;
        }

        private readonly Func<Smtp4devDbContext> dbContextFactory;

        private BlockingCollection<Action> processingQueue;

        private BlockingCollection<Action> priorityProcessingQueue;

        private DefaultServer smtpServer;

        private MessagesHub messagesHub;
        private SessionsHub sessionsHub;

        public void Start()
        {
            Smtp4devDbContext dbContent = dbContextFactory();

            
            foreach (Session unfinishedSession in dbContent.Sessions.Where(s => !s.EndDate.HasValue).ToArray())
            {
                unfinishedSession.EndDate = DateTime.Now;
            }
            dbContent.SaveChanges();

            TrimMessages(dbContent, serverOptions.Value);
            dbContent.SaveChanges();

            TrimSessions(dbContent, serverOptions.Value);
            dbContent.SaveChanges();
            messagesHub.OnMessagesChanged().Wait();
            sessionsHub.OnSessionsChanged().Wait();

            processingQueue = new BlockingCollection<Action>();
            priorityProcessingQueue = new BlockingCollection<Action>();
            Task.Run(ProcessingTaskWork);

            smtpServer.Start();

            string certDesc = string.IsNullOrEmpty(serverOptions.Value.TlsCertificate) ? "<self signed>" : serverOptions.Value.TlsCertificate;
            Console.WriteLine($"SMTP Server is listening on port {smtpServer.PortNumber} with TLS mode '{serverOptions.Value.TlsMode}' cert '{certDesc}'.\nKeeping last {serverOptions.Value.NumberOfMessagesToKeep} messages and {serverOptions.Value.NumberOfSessionsToKeep} sessions.");

        }
    }
}
