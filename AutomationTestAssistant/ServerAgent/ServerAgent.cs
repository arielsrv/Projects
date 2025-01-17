﻿using System;
using System.Collections.Concurrent;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using ATADataModel;
using AutomationTestAssistantCore;
using AutomationTestAssistantCore.ExecutionEngine.Contracts;
using AutomationTestAssistantCore.ExecutionEngine.Messages;

namespace ServerSocket
{
    public class ServerAgent : BaseLogger
    {
        private const string AgentSettingsInitializedMsg = "Agent Settings Initialized";
        private const string ExecutionTaskStartedMsg = "Execution Task Successfully  started";
        private const string MessageLoggerTaskStartedMsg = "Message Logger Task Successfully  started";
        private const string MessageLoggerTcpClientConnectedMsg = "Message Logger Task Tcp Client Started";
        private const string ClientMessageProcessorTcpClientConnectedMsg = "Client Message Processor Tcp Client Started";
        private const string ClientMessageProcessorTaskStartedMsg = "Client Message Processor Task Successfully  started";
        private const string ClientMessageMsBuildListnerTaskStartedMsg = "MsBuild Listner task Successfully  started";
        private static Process currentlyExecutedProc;
        private static Task executeCommandThreadWorker;
        private static Task msBuildLogListnerThreadWorker;
        private static Task messageLoggerThreadWorker;
        private static Task clientProcessMessageThreadWorker;
        private static Object lockObject;
        private static IpAddressSettings clientSettings;
        private static IpAddressSettings agentSettings;
        private static IpAddressSettings msBuildLogSettings;
        private static TcpListener agentListener;
        private static ConcurrentQueue<MessageArgsLogger> messagesToBeSend;
        private static ConcurrentQueue<string> commandsToBeExecuted;
        private static CancellationToken token;
        private static CancellationTokenSource cts;

        static void Main(string[] args)
        {
            while (true)
            {
                try
                {
                    InitializeSettings();
                    clientProcessMessageThreadWorker = Task.Factory.StartNew(() => ProcessClientMessage(agentListener), token, TaskCreationOptions.LongRunning, TaskScheduler.Current);
                    Console.WriteLine(ClientMessageProcessorTaskStartedMsg);
                    BaseLogger.Log.Info(ClientMessageProcessorTaskStartedMsg);
                    messageLoggerThreadWorker = Task.Factory.StartNew(() => SendLogMessages(agentListener, messagesToBeSend), token, TaskCreationOptions.LongRunning, TaskScheduler.Current);
                    Console.WriteLine(MessageLoggerTaskStartedMsg);
                    BaseLogger.Log.Info(MessageLoggerTaskStartedMsg);
                    executeCommandThreadWorker = Task.Factory.StartNew(() => ExecuteCommands(), token, TaskCreationOptions.LongRunning, TaskScheduler.Current);
                    Console.WriteLine(ExecutionTaskStartedMsg);
                    BaseLogger.Log.Info(ExecutionTaskStartedMsg);
                    msBuildLogListnerThreadWorker = Task.Factory.StartNew(() => ATACore.TcpWrapperProcessor.TcpMsBuildLoggerProcessor.ProcessMsBuildLoggerMessage(messagesToBeSend, msBuildLogSettings), TaskCreationOptions.LongRunning);
                    Console.WriteLine(ClientMessageMsBuildListnerTaskStartedMsg);
                    BaseLogger.Log.Info(ClientMessageMsBuildListnerTaskStartedMsg);
                    Task.WaitAll(new Task[] { clientProcessMessageThreadWorker, executeCommandThreadWorker, messageLoggerThreadWorker, msBuildLogListnerThreadWorker });
                }
                catch (AggregateException ae)
                {
                    ae = ae.Flatten();
                    ae.InnerExceptions.ToList().ForEach(ex => BaseLogger.Log.Error(ex.Message));
                }
                catch (TaskCanceledException ce)
                {
                    BaseLogger.Log.InfoFormat("Agent main/sub task(s) canceled: {0}", ce.InnerException);
                }
                finally
                {
                    agentListener.Stop();
                    CancelCurrentlyExecutingProcess();
                }
            }
        }

        private static void InitializeSettings()
        {
            clientSettings = new IpAddressSettings(ConfigurationManager.AppSettings["clientIp"], ConfigurationManager.AppSettings["clientPort"]);
            agentSettings = new IpAddressSettings(ConfigurationManager.AppSettings["agentIp"], ConfigurationManager.AppSettings["agentPort"]);
            msBuildLogSettings = new IpAddressSettings(ConfigurationManager.AppSettings["agentIp"], ConfigurationManager.AppSettings["msBuildAgentPort"]);
            Console.BackgroundColor = ConsoleColor.DarkGreen;
            commandsToBeExecuted = new ConcurrentQueue<string>();
            messagesToBeSend = new ConcurrentQueue<MessageArgsLogger>();
            cts = new CancellationTokenSource();
            token = cts.Token;
            currentlyExecutedProc = null;
            lockObject = new Object();         
            agentListener = new TcpListener(agentSettings.GetIPAddress(), clientSettings.Port);
            agentListener.Start();
            BaseLogger.Log.Info(AgentSettingsInitializedMsg);
            Console.WriteLine(AgentSettingsInitializedMsg);
        }

        private static void SendLogMessages(TcpListener agentListener, ConcurrentQueue<MessageArgsLogger> messagesToBeSend)
        {
            TcpClient agentTcpWriter = default(TcpClient);
            lock (lockObject)
                agentTcpWriter = agentListener.AcceptTcpClient();
            BaseLogger.Log.Info(MessageLoggerTcpClientConnectedMsg);
            Console.WriteLine(MessageLoggerTcpClientConnectedMsg);
            SendLogMessagesInternal(messagesToBeSend, agentTcpWriter);
        }

        private static void SendLogMessagesInternal(ConcurrentQueue<MessageArgsLogger> messagesToBeSend, TcpClient agentTcpWriter)
        {
            while (true)
            {
                SendLogMessagesProcessCancellation(agentTcpWriter);
                if (messagesToBeSend.Count > 0)
                {
                    MessageArgsLogger msgArgs;
                    bool isDequeued = messagesToBeSend.TryDequeue(out msgArgs);
                    if (isDequeued)
                    {
                        string messageToBeSend = ATACore.CommandExecutor.GenerateCurrentCommandParametersXml(msgArgs);
                        BaseLogger.Log.InfoFormat("\n\nMessage To BE Send:\n{0}\n\n", messageToBeSend);
                        ATACore.TcpWrapperProcessor.TcpClientWrapper.SendMessageToClient(agentTcpWriter, messageToBeSend);
                    }
                }
                Thread.Sleep(50);
            }
        }

        private static void SendLogMessagesProcessCancellation(TcpClient agentTcpWriter)
        {
            if (token.IsCancellationRequested)
            {
                agentTcpWriter.Close();
                token.ThrowIfCancellationRequested();
            }
        }

        private static void ExecuteCommands()
        {
            while (true)
            {
                if (commandsToBeExecuted.Count > 0 && (currentlyExecutedProc == null || currentlyExecutedProc.HasExited))
                {
                    string currentCommandXml = String.Empty;
                    bool dequeueSuccessfull = commandsToBeExecuted.TryDequeue(out currentCommandXml);
                    if (!dequeueSuccessfull)
                        continue;
                    Command currentAgentCommand = ATACore.CommandExecutor.GetCommandToBeExecutedFromMessage(currentCommandXml);
                    string dequeuedMsg = String.Format("Command {0} Dequeued on the Agent!", currentAgentCommand);
                    ATACore.CommandExecutor.EnqueueNewMessage(dequeuedMsg, MessageSource.DenqueueLogger, messagesToBeSend);
                    // Waits until the both threads are synchronized. The backgroudworker2 should be initialized again
                    //InitializeMsBuildLogger();
                    if (currentAgentCommand.Equals(Command.PARSE))
                    {
                        //currentlyExecutedProc = ATACore.CommandExecutor.QueueCommandToExecute(currentCommandXml);
                        //XmlSerializer deserializer = deserializer = new XmlSerializer(typeof(MessageArgsParseResult)); 
                        //StringReader textReader = new StringReader(currentCommandXml);
                        //MessageArgsParseResult msgArgParseResult = (MessageArgsParseResult)deserializer.Deserialize(textReader);
                        //Member currentMember = ATACore.Managers.MemberManager.GetMemberByUserName(ATACore.Managers.ContextManager.Context, msgArgParseResult.UserName);
                        //ATACore.Managers.ContextManager.Dispose();
                        //string executionResultRunId = ATACore.TestExecution.TestListParser.GetExecutionResultRunFromResultFile(msgArgParseResult.ResultsFilePath, currentMember.MemberId);
                        //ATACore.TestExecution.TestListParser.GetResultRunsFromResultFile(msgArgParseResult.ResultsFilePath, executionResultRunId);
                        //ATACore.CommandExecutor.EnqueueNewMessage(executionResultRunId, MessageSource.ResultsParser, messagesToBeSend);
                        ATACore.CommandExecutor.EnqueueNewMessage("ccb4dc49-7e20-4f12-a7b0-80cd643409af", MessageSource.ResultsParser, messagesToBeSend);
                    }
                    else
                    {
                        currentlyExecutedProc = ATACore.CommandExecutor.QueueCommandToExecute(currentCommandXml);
                    }

                    string agentResponseMessage = String.Concat("Start Executing ", ATACore.CommandExecutor.GetCommandToBeExecutedFromMessage(currentCommandXml));
                    ATACore.CommandExecutor.EnqueueNewMessage(agentResponseMessage, MessageSource.ExecutionLogger, messagesToBeSend);
                }
            }
        }

        private static void InitializeMsBuildLogger()
        {
            //msBuildLogListnerThreadWorker.Wait();
            // Locks the current thread until the agentMsBuildListner is initialized                     
            msBuildLogListnerThreadWorker = Task.Factory.StartNew(() => ATACore.TcpWrapperProcessor.TcpMsBuildLoggerProcessor.ProcessMsBuildLoggerMessage(messagesToBeSend, msBuildLogSettings), TaskCreationOptions.LongRunning);
            Thread.Sleep(1000);
        }

        private static void ProcessClientMessage(TcpListener agentListener)
        {   
            bool connected = true;
            TcpClient agentTcpListener = default(TcpClient);
            lock (lockObject)
                agentTcpListener = agentListener.AcceptTcpClient();
            BaseLogger.Log.Info(ClientMessageProcessorTcpClientConnectedMsg);
            Console.WriteLine(ClientMessageProcessorTcpClientConnectedMsg);
            ProcessClientMessageInternal(agentListener, connected, agentTcpListener);
        }

        private static void ProcessClientMessageInternal(TcpListener agentListener, bool connected, TcpClient agentTcpListener)
        {
            while (connected)
            {
                ClientMessageProcessCancellation(agentTcpListener);
                // Only one thread a time can use the Tcp objects and the listener because of that we use mutex to protect the shared resource. Only one thread a time can own the mutex.
                // we use the WaitOne to signal other threads that the resource is currently used by this thread. After work we release the mutex.
                string dataFromClient = ATACore.TcpWrapperProcessor.TcpClientWrapper.ReadLargeClientMessage(agentTcpListener);
                if (!String.IsNullOrEmpty(dataFromClient))
                    connected = ProcessCurrentAgentCommand(agentTcpListener, agentListener, dataFromClient);
            }
        }

        private static void ClientMessageProcessCancellation(TcpClient agentTcpListener)
        {
            if (token.IsCancellationRequested)
            {
                agentTcpListener.Close();
                token.ThrowIfCancellationRequested();
            }
        }

        private static bool ProcessCurrentAgentCommand(TcpClient agentTcpReader, TcpListener agentListener, string dataFromClient)
        {
            Command currentAgentCommand = ATACore.CommandExecutor.GetCommandToBeExecutedFromMessage(dataFromClient);
            bool connected = true;
            switch (currentAgentCommand)
            {
                case Command.DISCONNECT:
                    cts.Cancel();
                    break;
                case Command.KILL:
                    CancelCurrentlyExecutingProcess();
                    break;
                default:
                    Console.WriteLine(currentAgentCommand.ToString());
                    ProcessDefaultAgentCommand(dataFromClient);
                    break;
            }

            return connected;
        }

        private static void ProcessDefaultAgentCommand(string dataFromClient)
        {
            commandsToBeExecuted.Enqueue(dataFromClient);
            string queuedMsg = ATACore.CommandExecutor.GetCommandToBeExecutedFromMessage(dataFromClient).ToString();
            string agentResponseMessage = String.Format("Command {0} Enqueued on the Agent!", queuedMsg);
            BaseLogger.Log.Info(queuedMsg);
            ATACore.CommandExecutor.EnqueueNewMessage(agentResponseMessage, MessageSource.DenqueueLogger, messagesToBeSend);
        }

        private static void CancelCurrentlyExecutingProcess()
        {
            if (!currentlyExecutedProc.HasExited && currentlyExecutedProc != null)
            {
                string killMessage = String.Format("Process {0} with ID:{1} was canceled successfully on Machine: {2}", currentlyExecutedProc.ProcessName, currentlyExecutedProc.Id, Environment.MachineName);
                ATACore.CommandExecutor.EnqueueNewMessage(killMessage, MessageSource.ExecutionLogger, messagesToBeSend);
                BaseLogger.Log.Info(killMessage);
                currentlyExecutedProc.Kill();
            }
        }
    }
}