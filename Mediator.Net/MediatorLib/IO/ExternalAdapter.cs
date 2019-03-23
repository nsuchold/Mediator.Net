﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Ifak.Fast.Mediator.IO
{
    public abstract class ExternalAdapter : AdapterBase
    {
        protected AdapterCallback callback = null;

        private Process process = null;
        private Task taskReceive = null;
        private TcpConnectorMaster connection = null;
        private bool shutdown = false;
        protected abstract string GetCommand(Config config);
        protected abstract string GetArgs(Config config);
        private string adapterName = "";

        public override async Task<Group[]> Initialize(Adapter adapter, AdapterCallback callback, DataItemInfo[] itemInfos) {

            this.callback = callback;
            this.adapterName = adapter.Name;

            var config = new Config(adapter.Config);

            string cmd = GetCommand(config);
            string args = GetArgs(config);

            const string portPlaceHolder = "{PORT}";
            if (!args.Contains(portPlaceHolder)) throw new Exception("Missing port placeholder in args parameter: {PORT}");

            var server = TcpConnectorServer.ListenOnFreePort();
            int port = server.Port;

            args = args.Replace(portPlaceHolder, port.ToString());

            try {

                var taskConnect = server.WaitForConnect(TimeSpan.FromSeconds(60));

                process = StartProcess(cmd, args);

                while (!process.HasExited && !taskConnect.IsCompleted) {
                    await Task.Delay(TimeSpan.FromMilliseconds(50));
                }

                if (process.HasExited) {
                    throw new Exception($"Failed to start command \"{cmd}\" with arguments \"{args}\"");
                }

                connection = await taskConnect;

                var parentInfo = new ParentInfoMsg() { PID = Process.GetCurrentProcess().Id };
                Task ignored = SendVoidRequest(parentInfo);

                var initMsg = new InititializeMsg() {
                    Adapter = adapter,
                    ItemInfos = itemInfos
                };

                Task<Group[]> tInit = SendRequest<Group[]>(initMsg);

                taskReceive = connection.ReceiveAndDistribute(onEvent);

                Task t = await Task.WhenAny(tInit, taskReceive);

                if (t != tInit) {
                    if (process.HasExited)
                        throw new Exception("Adapter process terminated during Init call.");
                    else
                        throw new Exception("TCP connection broken to Adapter process during Init call.");
                }

                Group[] res = await tInit;

                Task ignored2 = Supervise();

                return res;
            }
            catch (Exception) {
                if (connection != null) {
                    connection.Close("Init failed.");
                }
                StopProcess(process);
                process = null;
                throw;
            }
            finally {
                server.StopListening();
            }
        }

        private async Task Supervise() {

            while (!shutdown && !taskReceive.IsCompleted && !process.HasExited) {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            if (shutdown || process == null) return;

            if (taskReceive.IsFaulted || process.HasExited) {
                Thread.Sleep(500); // no need for async wait here
                callback.Notify_NeedRestart($"External adapter {adapterName} terminated unexpectedly.");
            }
        }

        public override async Task<VTQ[]> ReadDataItems(string group, IList<ReadRequest> items, Duration? timeout) {
            var msg = new ReadDataItemsMsg() {
                Group = group,
                Items = items,
                Timeout = timeout
            };
            return await SendRequest<VTQ[]>(msg);
        }

        public override async Task<WriteDataItemsResult> WriteDataItems(string group, IList<DataItemValue> values, Duration? timeout) {
            var msg = new WriteDataItemsMsg() {
                Group = group,
                Values = values,
                Timeout = timeout
            };
            return await SendRequest<WriteDataItemsResult>(msg);
        }

        public override async Task<string[]> BrowseAdapterAddress() {
            var msg = new BrowseAdapterAddressMsg();
            return await SendRequest<string[]>(msg);
        }

        public override async Task<string[]> BrowseDataItemAddress(string idOrNull) {
            var msg = new BrowseDataItemAddressMsg() {
                IdOrNull = idOrNull
            };
            return await SendRequest<string[]>(msg);
        }

        public override async Task Shutdown() {

            shutdown = true;
            if (process == null) return;

            var taskAbort = SendVoidRequest(new ShutdownMsg());

            try {

                Timestamp tStart = Timestamp.Now;
                const int timeout = 12;

                while (!taskAbort.IsCompleted) {

                    if (process.HasExited) {
                        Console.Out.WriteLine("External adapter terminated unexpectedly during shutdown.");
                        break;
                    }

                    if (Timestamp.Now - tStart > Duration.FromSeconds(timeout)) {
                        Console.Out.WriteLine($"Adapter did not return from Shutdown within {timeout} seconds. Killing process...");
                        break;
                    }

                    await Task.WhenAny(taskAbort, Task.Delay(2000));

                    if (!taskAbort.IsCompleted) {
                        long secondsUntilTimeout = (tStart.AddSeconds(timeout) - Timestamp.Now).TotalMilliseconds / 1000;
                        Console.Out.WriteLine("Waiting for Shutdown completion (timeout in {0} seconds)...", secondsUntilTimeout);
                    }
                }
            }
            finally {
                connection.Close("Shutdown");
                StopProcess(process);
                process = null;
            }
        }

        private void onEvent(Event evt) {
            switch (evt.Code) {
                case AdapterMsg.ID_Event_AlarmOrEvent:
                    var alarm = StdJson.ObjectFromUtf8Stream<AdapterAlarmOrEvent>(evt.Payload);
                    callback.Notify_AlarmOrEvent(alarm);
                    break;

                case AdapterMsg.ID_Event_DataItemsChanged:
                    var items = StdJson.ObjectFromUtf8Stream<DataItemValue[]>(evt.Payload);
                    callback.Notify_DataItemsChanged(items);
                    break;

                default:
                    Console.Error.WriteLine("Unknown event code: " + evt.Code);
                    break;
            }
        }

        private async Task<T> SendRequest<T>(AdapterMsg requestMsg) {
            using (Response res = await connection.SendRequest(requestMsg.GetMessageCode(), stream => StdJson.ObjectToStream(requestMsg, stream))) {
                if (res.Success) {
                    return StdJson.ObjectFromUtf8Stream<T>(res.SuccessPayload);
                }
                else {
                    throw new Exception(res.ErrorMsg);
                }
            }
        }

        private async Task SendVoidRequest(AdapterMsg requestMsg) {
            using (Response res = await connection.SendRequest(requestMsg.GetMessageCode(), stream => StdJson.ObjectToStream(requestMsg, stream))) {
                if (res.Success) {
                    return;
                }
                else {
                    throw new Exception(res.ErrorMsg);
                }
            }
        }

        private Process StartProcess(string fileName, string args) {
            Process process = new Process();
            process.StartInfo.FileName = fileName;
            process.StartInfo.Arguments = args;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.OutputDataReceived += OnReceivedOutput;
            process.ErrorDataReceived += OnReceivedError;
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }

        private void OnReceivedOutput(object sender, DataReceivedEventArgs e) {
            if (!string.IsNullOrEmpty(e.Data)) {
                Console.Out.WriteLine(e.Data);
            }
        }

        private void OnReceivedError(object sender, DataReceivedEventArgs e) {
            if (!string.IsNullOrEmpty(e.Data)) {
                Console.Error.WriteLine(e.Data);
            }
        }

        private void StopProcess(Process p) {
            if (p == null || p.HasExited) return;
            try {
                p.Kill();
            }
            catch (Exception exp) {
                Console.Out.WriteLine("StopProcess: " + exp.Message);
            }
        }
    }

    internal abstract class AdapterMsg
    {
        public const byte ID_Event_AlarmOrEvent = 1;
        public const byte ID_Event_DataItemsChanged = 2;

        public const byte ID_ParentInfo = 99;
        public const byte ID_Initialize = 1;
        public const byte ID_ReadDataItems = 2;
        public const byte ID_WriteDataItems = 3;
        public const byte ID_BrowseAdapterAddress = 4;
        public const byte ID_BrowseDataItemAddress = 5;
        public const byte ID_Shutdown = 6;

        public abstract byte GetMessageCode();
    }

    internal class ParentInfoMsg : AdapterMsg
    {
        public int PID { get; set; }

        public override byte GetMessageCode() => ID_ParentInfo;
    }

    internal class InititializeMsg : AdapterMsg
    {
        public Adapter Adapter { get; set; }
        public DataItemInfo[] ItemInfos { get; set; }

        public override byte GetMessageCode() => ID_Initialize;
    }

    internal class ReadDataItemsMsg : AdapterMsg
    {
        public string Group { get; set; }
        public IList<ReadRequest> Items { get; set; }
        public Duration? Timeout { get; set; }

        public override byte GetMessageCode() => ID_ReadDataItems;
    }

    internal class WriteDataItemsMsg : AdapterMsg
    {
        public string Group { get; set; }
        public IList<DataItemValue> Values { get; set; }
        public Duration? Timeout { get; set; }

        public override byte GetMessageCode() => ID_WriteDataItems;
    }

    internal class BrowseAdapterAddressMsg : AdapterMsg
    {
        public override byte GetMessageCode() => ID_BrowseAdapterAddress;
    }

    internal class BrowseDataItemAddressMsg : AdapterMsg
    {
        public string IdOrNull { get; set; }

        public override byte GetMessageCode() => ID_BrowseDataItemAddress;
    }

    internal class ShutdownMsg : AdapterMsg
    {
        public override byte GetMessageCode() => ID_Shutdown;
    }
}
