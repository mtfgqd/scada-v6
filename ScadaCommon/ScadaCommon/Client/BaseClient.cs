﻿/*
 * Copyright 2021 Rapid Software LLC
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * 
 *     http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 * 
 * 
 * Product  : Rapid SCADA
 * Module   : ScadaCommon
 * Summary  : Represents the base class for TCP clients which interacts with a server
 * 
 * Author   : Mikhail Shiryaev
 * Created  : 2020
 * Modified : 2021
 */

using Scada.Lang;
using Scada.Log;
using Scada.Protocol;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using static Scada.BinaryConverter;
using static Scada.Protocol.ProtocolUtils;

namespace Scada.Client
{
    /// <summary>
    /// Represents the base class for TCP clients which interacts with a server.
    /// <para>Представляет базовый класс TCP-клиентов, которые взаимодействуют с сервером.</para>
    /// </summary>
    /// <remarks>The class is not thread safe.</remarks>
    public abstract class BaseClient
    {
        /// <summary>
        /// The maximum number of packet bytes to write to the communication log.
        /// </summary>
        protected const int MaxLoggingSize = 100;
        /// <summary>
        /// The period when reconnection is allowed.
        /// </summary>
        protected readonly TimeSpan ReconnectPeriod = TimeSpan.FromSeconds(5);
        /// <summary>
        /// The period of checking connection.
        /// </summary>
        protected readonly TimeSpan PingPeriod = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The input data buffer.
        /// </summary>
        protected readonly byte[] inBuf;
        /// <summary>
        /// The output data buffer.
        /// </summary>
        protected readonly byte[] outBuf;

        /// <summary>
        /// The client connection.
        /// </summary>
        protected TcpClient tcpClient;
        /// <summary>
        /// The stream for network access.
        /// </summary>
        protected NetworkStream netStream;
        /// <summary>
        /// The transaction ID counter.
        /// </summary>
        protected ushort transactionID;
        /// <summary>
        /// The connection attempt time (UTC).
        /// </summary>
        protected DateTime connAttemptDT;
        /// <summary>
        /// The last successful response time (UTC).
        /// </summary>
        protected DateTime responseDT;


        /// <summary>
        /// Initializes a new instance of the class.
        /// </summary>
        public BaseClient(ConnectionOptions connectionOptions)
        {
            inBuf = new byte[BufferLenght];
            outBuf = new byte[BufferLenght];

            tcpClient = null;
            netStream = null;
            transactionID = 0;
            connAttemptDT = DateTime.MinValue;
            responseDT = DateTime.MinValue;

            ConnectionOptions = connectionOptions ?? throw new ArgumentNullException(nameof(connectionOptions));
            CommLog = null;
            ClientState = ClientState.Disconnected;
            SessionID = 0;
            ServerName = "";
            UserID = 0;
            RoleID = 0;
        }


        /// <summary>
        /// Gets the connection options.
        /// </summary>
        public ConnectionOptions ConnectionOptions { get; }

        /// <summary>
        /// Gets the sets the detailed communication log.
        /// </summary>
        public ILog CommLog { get; set; }

        /// <summary>
        /// Gets the client communication state.
        /// </summary>
        public ClientState ClientState { get; protected set; }

        /// <summary>
        /// Gets a value indicating whether the client is ready for communication.
        /// </summary>
        public bool IsReady
        {
            get
            {
                return ClientState == ClientState.LoggedIn || DateTime.UtcNow - connAttemptDT > ReconnectPeriod;
            }
        }

        /// <summary>
        /// Gets the time (UTC) that may be considered as the time when the client was active.
        /// </summary>
        public DateTime LastActivityTime
        {
            get
            {
                return responseDT;
            }
        }

        /// <summary>
        /// Gets the session ID.
        /// </summary>
        public long SessionID { get; protected set; }

        /// <summary>
        /// Gets the server name and version.
        /// </summary>
        public string ServerName { get; protected set; }

        /// <summary>
        /// Get the user ID.
        /// </summary>
        public int UserID { get; protected set; }

        /// <summary>
        /// Get the user role ID.
        /// </summary>
        public int RoleID { get; protected set; }


        /// <summary>
        /// Connects and authenticates with the server.
        /// </summary>
        protected void Connect()
        {
            // create connection
            tcpClient = new TcpClient
            {
                SendTimeout = ConnectionOptions.Timeout,
                ReceiveTimeout = ConnectionOptions.Timeout
            };

            // connect
            CommLog?.WriteAction("Connect to " + ConnectionOptions.Host);

            if (IPAddress.TryParse(ConnectionOptions.Host, out IPAddress address))
                tcpClient.Connect(address, ConnectionOptions.Port);
            else
                tcpClient.Connect(ConnectionOptions.Host, ConnectionOptions.Port);

            netStream = tcpClient.GetStream();
            ClientState = ClientState.Connected;
        }

        /// <summary>
        /// Disconnects from the server.
        /// </summary>
        protected void Disconnect()
        {
            if (tcpClient != null)
            {
                CommLog?.WriteAction("Disconnect");

                if (netStream != null)
                {
                    ClearNetStream(netStream, inBuf); // to disconnect correctly
                    netStream.Close();
                    netStream = null;
                }

                tcpClient.Close();
                tcpClient = null;

                ClientState = ClientState.Disconnected;
                SessionID = 0;
                ServerName = "";
            }
        }

        /// <summary>
        /// Restores a connection with the server.
        /// </summary>
        protected void RestoreConnection()
        {
            DateTime utcNow = DateTime.UtcNow;
            bool connectNeeded = false;

            if (ClientState == ClientState.LoggedIn)
            {
                if (utcNow - responseDT > PingPeriod)
                {
                    try
                    {
                        // check connection
                        DataPacket request = CreateRequest(FunctionID.GetStatus, 10);
                        SendRequest(request);
                        ReceiveResponse(request);
                    }
                    catch
                    {
                        connectNeeded = true;
                    }
                }
            }
            else if (utcNow - connAttemptDT > ReconnectPeriod)
            {
                connectNeeded = true;
            }

            try
            {
                if (connectNeeded)
                {
                    connAttemptDT = utcNow;
                    Disconnect();
                    Connect();

                    GetSessionInfo(out long sessionID, out ushort protocolVersion, out string serverName);
                    SessionID = sessionID;
                    ServerName = serverName;

                    if (protocolVersion != ProtocolVersion)
                    {
                        throw new ScadaException(Locale.IsRussian ?
                            "Несовместимая версия протокола." :
                            "Incompatible protocol version.");
                    }

                    Login(ConnectionOptions.Username, ConnectionOptions.Password, 
                        out bool loggedIn, out int userID, out int roleID, out string errMsg);
                    UserID = userID;
                    RoleID = roleID;

                    if (loggedIn)
                    {
                        ClientState = ClientState.LoggedIn;
                        CommLog?.WriteAction("User is logged in");
                    }
                    else
                    {
                        throw new ScadaException(errMsg);
                    }
                }
                else if (ClientState == ClientState.LoggedIn)
                {
                    ClearNetStream(netStream, inBuf);
                }
                else
                {
                    throw new ScadaException(Locale.IsRussian ?
                        "Клиент не вошёл в систему. Попробуйте позже." :
                        "Client is not logged in. Try again later.");
                }
            }
            catch
            {
                ClientState = ClientState.Error;
                throw;
            }
        }

        /// <summary>
        /// Creates a new data packet for request.
        /// </summary>
        protected DataPacket CreateRequest(ushort functionID, int dataLength = 0, bool nextTransaction = true)
        {
            if (nextTransaction)
                transactionID++;

            return new DataPacket
            {
                TransactionID = transactionID,
                DataLength = dataLength,
                SessionID = SessionID,
                FunctionID = functionID,
                Buffer = outBuf
            };
        }

        /// <summary>
        /// Sends the request to the server.
        /// </summary>
        protected void SendRequest(DataPacket request)
        {
            try
            {
                request.Encode();
                netStream.Write(request.Buffer, 0, request.BufferLength);

                if (CommLog != null)
                {
                    CommLog.WriteAction(FunctionID.GetName(request.FunctionID));
                    CommLog.WriteAction(BuildWritingText(request.Buffer, 0, request.BufferLength));
                }
            }
            catch (IOException)
            {
                ClientState = ClientState.Error;
                throw;
            }
        }

        /// <summary>
        /// Receives a response from the server.
        /// </summary>
        protected DataPacket ReceiveResponse(DataPacket request)
        {
            DataPacket response = null;
            bool formatError = true;
            string errDescr = "";
            int bytesToRead = HeaderLength + 2;
            int bytesRead;

            try 
            { 
                bytesRead = netStream.Read(inBuf, 0, bytesToRead);
                CommLog?.WriteAction(BuildReadingText(inBuf, 0, bytesToRead, bytesRead));
            }
            catch (IOException)
            { 
                ClientState = ClientState.Error; 
                throw; 
            }

            if (bytesRead == bytesToRead)
            {
                response = new DataPacket
                {
                    TransactionID = BitConverter.ToUInt16(inBuf, 0),
                    DataLength = BitConverter.ToInt32(inBuf, 2),
                    SessionID = BitConverter.ToInt64(inBuf, 6),
                    FunctionID = BitConverter.ToUInt16(inBuf, 14),
                    Buffer = inBuf
                };

                if (response.DataLength + 6 > inBuf.Length)
                {
                    errDescr = Locale.IsRussian ?
                        "длина данных слишком велика" :
                        "data length is too big";
                }
                else if (response.TransactionID != request.TransactionID)
                {
                    errDescr = Locale.IsRussian ?
                        "неверный идентификатор транзакции" :
                        "incorrect transaction ID";
                }
                else if (response.SessionID != SessionID && SessionID != 0)
                {
                    errDescr = Locale.IsRussian ?
                        "неверный идентификатор сессии" :
                        "incorrect session ID";
                }
                else if ((response.FunctionID & 0x7FFF) != request.FunctionID)
                {
                    errDescr = Locale.IsRussian ?
                        "неверный идентификатор функции" :
                        "incorrect function ID";
                }
                else
                {
                    // read the rest of the data
                    bytesToRead = response.DataLength - 10;
                    bytesRead = ReadData(netStream, tcpClient.ReceiveTimeout, inBuf, HeaderLength + 2, bytesToRead);
                    CommLog?.WriteAction(BuildReadingText(inBuf, HeaderLength + 2, bytesToRead, bytesRead));

                    if (bytesRead == bytesToRead)
                    {
                        formatError = false;
                        responseDT = DateTime.UtcNow;

                        // handle error response
                        if ((response.FunctionID & 0x8000) > 0)
                        {
                            ErrorCode errorCode = (ErrorCode)inBuf[ArgumentIndex];
                            CommLog?.WriteError(errorCode.ToString());
                            throw new ProtocolException(errorCode);
                        }
                    }
                    else
                    {
                        errDescr = Locale.IsRussian ?
                            "не удалось прочитать все данные" :
                            "unable to read all data";
                    }
                }
            }
            else
            {
                errDescr = Locale.IsRussian ?
                    "не удалось прочитать заголовок пакета данных" :
                    "unable to read data packet header";
            }

            if (formatError)
            {
                throw new ScadaException(Locale.IsRussian ?
                    "Некорректный формат данных, полученных от сервера: {0}" :
                    "Incorrect format of data received from the server: {0}", errDescr);
            }

            return response;
        }

        /// <summary>
        /// Builds text for reading logging.
        /// </summary>
        protected string BuildReadingText(byte[] buffer, int index, int bytesToRead, int bytesRead)
        {
            return "Receive (" + bytesRead + "/" + bytesToRead + "): " +
                ScadaUtils.BytesToString(buffer, index, Math.Min(bytesRead, MaxLoggingSize)) +
                (bytesRead <= MaxLoggingSize ? "" : "...") +
                (bytesToRead > 0 ? "" : "no data");
        }

        /// <summary>
        /// Builds text for writing logging.
        /// </summary>
        protected string BuildWritingText(byte[] buffer, int index, int bytesToWrite)
        {
            return "Send (" + bytesToWrite + "): " +
                ScadaUtils.BytesToString(buffer, index, Math.Min(bytesToWrite, MaxLoggingSize)) +
                (bytesToWrite <= MaxLoggingSize ? "" : "...");
        }

        /// <summary>
        /// Gets the information about the current session.
        /// </summary>
        protected void GetSessionInfo(out long sessionID, out ushort protocolVersion, out string serverName)
        {
            DataPacket request = CreateRequest(FunctionID.GetSessionInfo, 10);
            SendRequest(request);

            DataPacket response = ReceiveResponse(request);
            sessionID = response.SessionID;
            int index = ArgumentIndex;
            protocolVersion = GetUInt16(inBuf, ref index);
            serverName = GetString(inBuf, ref index);
        }

        /// <summary>
        /// Logins to the server.
        /// </summary>
        protected void Login(string username, string password, 
            out bool loggedIn, out int userID, out int roleID, out string errMsg)
        {
            DataPacket request = CreateRequest(FunctionID.Login);
            int index = ArgumentIndex;
            CopyString(username, outBuf, ref index);
            CopyString(EncryptPassword(password, SessionID, ConnectionOptions.SecretKey), outBuf, ref index);
            CopyString(ConnectionOptions.Instance, outBuf, ref index);
            request.BufferLength = index;
            SendRequest(request);

            ReceiveResponse(request);
            index = ArgumentIndex;
            loggedIn = GetBool(inBuf, ref index);
            userID = GetInt32(inBuf, ref index);
            roleID = GetInt32(inBuf, ref index);
            errMsg = GetString(inBuf, index);
        }

        /// <summary>
        /// Raises the Progress event.
        /// </summary>
        protected void OnProgress(int blockNumber, int blockCount)
        {
            Progress?.Invoke(this, new ProgressEventArgs(blockNumber, blockCount));
        }


        /// <summary>
        /// Gets the server and the session status.
        /// </summary>
        public void GetStatus(out bool serverIsReady, out bool userIsLoggedIn)
        {
            RestoreConnection();

            DataPacket request = CreateRequest(FunctionID.GetStatus, 10);
            SendRequest(request);

            ReceiveResponse(request);
            serverIsReady = inBuf[ArgumentIndex] > 0;
            userIsLoggedIn = inBuf[ArgumentIndex + 1] > 0;
        }

        /// <summary>
        /// Terminates the client session.
        /// </summary>
        public void TerminateSession()
        {
            RestoreConnection();
            DataPacket request = CreateRequest(FunctionID.TerminateSession, 10);
            SendRequest(request);
            ReceiveResponse(request);
            Disconnect();
        }

        /// <summary>
        /// Closes the client connection without notifying the server.
        /// </summary>
        public void Close()
        {
            Disconnect();
        }

        /// <summary>
        /// Gets the information about the file.
        /// </summary>
        public ShortFileInfo GetFileInfo(RelativePath relativePath)
        {
            RestoreConnection();

            DataPacket request = CreateRequest(FunctionID.GetFileInfo);
            int index = ArgumentIndex;
            CopyFileName(relativePath.DirectoryID, relativePath.Path, outBuf, ref index);
            request.BufferLength = index;
            SendRequest(request);

            ReceiveResponse(request);
            index = ArgumentIndex;
            return new ShortFileInfo
            {
                Exists = GetBool(inBuf, ref index),
                LastWriteTime = GetTime(inBuf, ref index),
                Length = GetInt64(inBuf, ref index)
            };
        }

        /// <summary>
        /// Downloads the file.
        /// </summary>
        public void DownloadFile(RelativePath relativePath, long offset, int count, bool readFromEnd,
            DateTime newerThan, bool throwOnFail, Func<Stream> createStreamFunc,
            out DateTime lastWriteTime, out FileReadingResult readingResult, out Stream stream)
        {
            if (createStreamFunc == null)
                throw new ArgumentNullException(nameof(createStreamFunc));

            RestoreConnection();

            DataPacket request = CreateRequest(FunctionID.DownloadFile);
            int index = ArgumentIndex;
            CopyFileName(relativePath.DirectoryID, relativePath.Path, outBuf, ref index);
            CopyInt64(offset, outBuf, ref index);
            CopyInt32(count, outBuf, ref index);
            CopyBool(readFromEnd, outBuf, ref index);
            CopyTime(newerThan, outBuf, ref index);
            request.BufferLength = index;
            SendRequest(request);

            int prevBlockNumber = 0;
            lastWriteTime = DateTime.MinValue;
            readingResult = FileReadingResult.BlockRead;
            stream = null;

            try
            {
                while (readingResult == FileReadingResult.BlockRead)
                {
                    ReceiveResponse(request);
                    index = ArgumentIndex;
                    int blockNumber = GetInt32(inBuf, ref index);
                    int blockCount = GetInt32(inBuf, ref index);
                    lastWriteTime = GetTime(inBuf, ref index);
                    readingResult = (FileReadingResult)GetByte(inBuf, ref index);

                    if (blockNumber != prevBlockNumber + 1)
                        ThrowBlockNumberException();

                    if (readingResult == FileReadingResult.BlockRead ||
                        readingResult == FileReadingResult.Completed)
                    {
                        if (stream == null)
                            stream = createStreamFunc();

                        int bytesToWrite = GetInt32(inBuf, ref index);
                        stream.Write(inBuf, index, bytesToWrite);
                    }

                    prevBlockNumber = blockNumber;
                    OnProgress(blockNumber, blockCount);
                }

                if (throwOnFail && readingResult != FileReadingResult.Completed)
                {
                    throw new ProtocolException(ErrorCode.InternalServerError, string.Format(Locale.IsRussian ?
                        "Ошибка при чтении файла {0}: {1}" :
                        "Error reading file {0}: {1}", relativePath, readingResult.ToString(Locale.IsRussian)));
                }
            }
            catch
            {
                stream?.Dispose();
                stream = null;
                throw;
            }
        }

        /// <summary>
        /// Downloads the file.
        /// </summary>
        public void DownloadFile(RelativePath relativePath, long offset, int count, bool readFromEnd,
            DateTime newerThan, string destFileName, out DateTime lastWriteTime, out FileReadingResult readingResult)
        {
            DownloadFile(relativePath, offset, count, readFromEnd, newerThan, false,
                () => { return new FileStream(destFileName, FileMode.Create, FileAccess.Write, FileShare.Read); },
                out lastWriteTime, out readingResult, out Stream stream);
            stream?.Dispose();
        }

        /// <summary>
        /// Downloads the file.
        /// </summary>
        public void DownloadFile(RelativePath relativePath, Stream stream, out FileReadingResult readingResult)
        {
            DownloadFile(relativePath, 0, 0, false, DateTime.MinValue, false, () => stream, 
                out _, out readingResult, out _);
        }

        /// <summary>
        /// Downloads the file or throws an exception on failure.
        /// </summary>
        public bool DownloadFile(RelativePath relativePath, Stream stream, bool throwOnFail = false)
        {
            DownloadFile(relativePath, 0, 0, false, DateTime.MinValue, throwOnFail, () => stream, 
                out _, out FileReadingResult readingResult, out _);
            return readingResult == FileReadingResult.Completed;
        }

        /// <summary>
        /// Uploads the file.
        /// </summary>
        public void UploadFile(string srcFileName, RelativePath destPath, out bool fileAccepted)
        {
            if (!File.Exists(srcFileName))
                throw new ScadaException(CommonPhrases.FileNotFound);

            RestoreConnection();

            using (FileStream stream =
                new FileStream(srcFileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                // request permission to upload file
                const int FileDataIndex = ArgumentIndex + 9;
                const int BlockCapacity = BufferLenght - FileDataIndex;
                long bytesToReadTotal = stream.Length;
                int blockCount = (int)Math.Ceiling((double)bytesToReadTotal / BlockCapacity);

                DataPacket request = CreateRequest(FunctionID.UploadFile);
                int index = ArgumentIndex;
                CopyInt32(0, outBuf, ref index);
                CopyInt32(blockCount, outBuf, ref index);
                CopyFileName(destPath.DirectoryID, destPath.Path, outBuf, ref index);
                request.BufferLength = index;
                SendRequest(request);

                ReceiveResponse(request);
                fileAccepted = inBuf[ArgumentIndex] > 0;

                // upload file
                if (fileAccepted)
                {
                    int blockNumber = 0;
                    long bytesReadTotal = 0;
                    bool endOfFile = false;
                    request = null;

                    while (!endOfFile)
                    {
                        // read from file
                        int bytesToRead = (int)Math.Min(bytesToReadTotal - bytesReadTotal, BlockCapacity);
                        int bytesRead = stream.Read(outBuf, FileDataIndex, bytesToRead);
                        bytesReadTotal += bytesRead;
                        endOfFile = bytesRead < bytesToRead || bytesReadTotal == bytesToReadTotal;

                        // send data
                        request = CreateRequest(FunctionID.UploadFile, 0, false);
                        index = ArgumentIndex;
                        CopyInt32(++blockNumber, outBuf, ref index);
                        CopyBool(endOfFile, outBuf, ref index);
                        CopyInt32(bytesRead, outBuf, ref index);
                        request.BufferLength = FileDataIndex + bytesRead;
                        SendRequest(request);
                        OnProgress(blockNumber, blockCount);
                    }

                    if (request != null)
                        ReceiveResponse(request);
                }
            }
        }


        /// <summary>
        /// Occurs during the progress of a long-term operation.
        /// </summary>
        public event EventHandler<ProgressEventArgs> Progress;
    }
}
