﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace CurlUnity
{
    public class CurlEasy : IDisposable
    {
        public delegate void PerformCallback(CurlEasy easy);

        public string url { get; set; }
        public string method { get; set; } = "GET";
        public string contentType { get; set; } = "application/text";
        public string outputPath { get; set; }
        public int timeout { get; set; } = 10000;
        public int maxRetryCount { get; set; } = 5;
        public bool useHttp2 { get; set; }
        public bool insecure { get; set; }
        public byte[] outData { get; set; }
        public byte[] inData { get; private set; }
        public string httpVersion { get; private set; }
        public int status { get; private set; }
        public string message { get; private set; }
        public bool running { get; private set; }
        public bool debug { get; set; }
        public PerformCallback performCallback;

        private IntPtr easyPtr;
        private int retryCount;
        private Dictionary<string, string> userHeader;
        private Dictionary<string, string> outHeader;
        private Dictionary<string, string> inHeader;

        private Stream responseHeaderStream;
        private Stream responseBodyStream;

        private GCHandle thisHandle;

        private static string s_capath;

        static CurlEasy()
        {
            s_capath = Path.Combine(Application.persistentDataPath, "cacert");
            if (!File.Exists(s_capath))
            {
                File.WriteAllBytes(s_capath, Resources.Load<TextAsset>("cacert").bytes);
            }
            Lib.curl_global_init((long)CURLGLOBAL.ALL);
        }

        public CurlEasy(IntPtr ptr = default(IntPtr))
        {
            if (ptr != IntPtr.Zero)
            {
                easyPtr = ptr;
            }
            else
            {
                easyPtr = Lib.curl_easy_init();
            }
        }

        public void Dispose()
        {
            Lib.curl_easy_cleanup(easyPtr);
        }

        public void Reset()
        {
            Lib.curl_easy_reset(easyPtr);
        }

        public CurlEasy Duplicate()
        {
            return new CurlEasy(Lib.curl_easy_duphandle(easyPtr));
        }

        #region SetOpt
        public CURLE SetOpt(CURLOPT options, IntPtr value)
        {
            return Lib.curl_easy_setopt_ptr(easyPtr, options, value);
        }

        public CURLE SetOpt(CURLOPT options, string value)
        {
            return Lib.curl_easy_setopt_str(easyPtr, options, value);
        }

        public CURLE SetOpt(CURLOPT options, byte[] value)
        {
            return Lib.curl_easy_setopt_ptr(easyPtr, options, value);
        }

        public CURLE SetOpt(CURLOPT options, bool value)
        {
            return Lib.curl_easy_setopt_int(easyPtr, options, value);
        }

        public CURLE SetOpt(CURLOPT options, long value)
        {
            return Lib.curl_easy_setopt_int(easyPtr, options, value);
        }

        public CURLE SetOpt(CURLOPT options, Delegates.WriteFunction value)
        {
            return Lib.curl_easy_setopt_ptr(easyPtr, options, value);
        }

        public CURLE SetOpt(CURLOPT options, Delegates.HeaderFunction value)
        {
            return Lib.curl_easy_setopt_ptr(easyPtr, options, value);
        }

        public CURLE SetOpt(CURLOPT options, Delegates.DebugFunction value)
        {
            return Lib.curl_easy_setopt_ptr(easyPtr, options, value);
        }

        #endregion
        #region GetInfo
        public CURLE GetInfo(CURLINFO info, out long value)
        {
            value = 0;
            return Lib.curl_easy_getinfo_ptr(easyPtr, info, ref value);
        }

        public CURLE GetInfo(CURLINFO info, out double value)
        {
            value = 0;
            return Lib.curl_easy_getinfo_ptr(easyPtr, info, ref value);
        }

        public CURLE GetInfo(CURLINFO info, out string value)
        {
            value = null;
            IntPtr ptr = IntPtr.Zero;
            var result = Lib.curl_easy_getinfo_ptr(easyPtr, info, ref ptr);
            if (ptr != IntPtr.Zero)
            {
                unsafe
                {
                    value = Marshal.PtrToStringAnsi((IntPtr)ptr.ToPointer());
                }
            }
            return result;
        }

        public CURLE GetInfo(CURLINFO info, out CurlSlist value)
        {
            value = null;
            IntPtr ptr = IntPtr.Zero;
            var result = Lib.curl_easy_getinfo_ptr(easyPtr, info, ref ptr);
            value = new CurlSlist(ptr);
            return result;
        }
        #endregion

        public void Perform(CurlMulti multi, PerformCallback callback)
        {
            if (!running)
            {
                running = true;
                retryCount = maxRetryCount;
                performCallback = callback;
                Prepare();
                multi.AddHandle(this);
            }
            else
            {
                Debug.LogError("Can't preform a running handle again!");
            }
        }

        public void OnPerformComplete(CURLE result, CurlMulti multi)
        {
            thisHandle.Free();

            var done = false;

            if (result == CURLE.OK)
            {
                ProcessResponse();

                if (status == 200)
                {
                    done = true;
                }
                else if (status / 100 == 3)
                {
                    if (GetInfo(CURLINFO.REDIRECT_URL, out string location) == CURLE.OK)
                    {
                        url = location;
                    }
                }
            }
            else
            {
                Debug.LogWarning($"Failed to request: {url}, reason: {result}");
            }

            if (done || --retryCount < 0)
            {
                if (debug) Dump();
                performCallback?.Invoke(this);
                performCallback = null;
                running = false;
            }
            else
            {
                Prepare();
                multi.AddHandle(this);
            }
        }

        private void Prepare()
        {
            status = 0;
            message = null;

            thisHandle = GCHandle.Alloc(this);

            SetOpt(CURLOPT.URL, url);
            SetOpt(CURLOPT.CUSTOMREQUEST, method);

            if (useHttp2)
            {
                SetOpt(CURLOPT.HTTP_VERSION, (long)HTTPVersion.VERSION_2_0);
                SetOpt(CURLOPT.PIPEWAIT, true);
            }

            if (insecure)
            {
                SetOpt(CURLOPT.SSL_VERIFYHOST, false);
                SetOpt(CURLOPT.SSL_VERIFYPEER, false);
            }

            // Ca cert path
            SetOpt(CURLOPT.CAINFO, s_capath);

            // Fill request header
            var requestHeader = new CurlSlist(IntPtr.Zero);
            requestHeader.Append($"Content-Type:{contentType}");
            if (this.userHeader != null)
            {
                foreach (var entry in this.userHeader)
                {
                    requestHeader.Append(entry.Key + ":" + entry.Value);
                }
            }

            SetOpt(CURLOPT.HTTPHEADER, (IntPtr)requestHeader);
            // Fill request body
            if (outData != null && outData.Length > 0)
            {
                SetOpt(CURLOPT.POSTFIELDS, outData);
                SetOpt(CURLOPT.POSTFIELDSIZE, outData.Length);
            }

            // Handle response header
            responseHeaderStream = new MemoryStream();
            SetOpt(CURLOPT.HEADERFUNCTION, (Delegates.HeaderFunction)HeaderFunction);
            SetOpt(CURLOPT.HEADERDATA, (IntPtr)thisHandle);

            // Handle response body
            if (string.IsNullOrEmpty(outputPath))
            {
                responseBodyStream = new MemoryStream();
            }
            else
            {
                var dir = Path.GetDirectoryName(outputPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                responseBodyStream = new FileStream(outputPath, FileMode.OpenOrCreate);
            }
            SetOpt(CURLOPT.WRITEFUNCTION, (Delegates.WriteFunction)WriteFunction);
            SetOpt(CURLOPT.WRITEDATA, (IntPtr)thisHandle);

            // Debug
            if (debug)
            {
                outHeader = null;
                SetOpt(CURLOPT.VERBOSE, true);
                SetOpt(CURLOPT.DEBUGFUNCTION, DebugFunction);
                SetOpt(CURLOPT.DEBUGDATA, (IntPtr)thisHandle);
            }

            // Timeout
            SetOpt(CURLOPT.TIMEOUT_MS, timeout);
        }

        private void ProcessResponse()
        {
            inHeader = new Dictionary<string, string>();

            responseHeaderStream.Position = 0;
            var sr = new StreamReader(responseHeaderStream);

            // Handle first line
            {
                var line = sr.ReadLine();
                var index = line.IndexOf(' ');
                httpVersion = line.Substring(0, index);
                var nextIndex = line.IndexOf(' ', index + 1);
                if (int.TryParse(line.Substring(index + 1, nextIndex - index), out var _status))
                {
                    status = _status;
                }
                message = line.Substring(nextIndex + 1);
            }

            while (true)
            {
                var line = sr.ReadLine();
                if (!string.IsNullOrEmpty(line))
                {
                    var index = line.IndexOf(':');
                    var key = line.Substring(0, index).Trim();
                    var value = line.Substring(index + 1).Trim();
                    inHeader[key] = value;
                }
                else
                {
                    break;
                }
            }
            responseHeaderStream.Close();
            responseHeaderStream = null;

            var ms = responseBodyStream as MemoryStream;
            inData = ms?.ToArray();
            responseBodyStream.Close();
            responseBodyStream = null;
        }

        private void Dump()
        {
            var sb = new StringBuilder();

            GetInfo(CURLINFO.EFFECTIVE_URL, out string effectiveUrl);
            GetInfo(CURLINFO.CONTENT_LENGTH_UPLOAD, out double updateSize);
            GetInfo(CURLINFO.CONTENT_LENGTH_DOWNLOAD, out double downloadSize);
            GetInfo(CURLINFO.TOTAL_TIME, out double time);

            sb.AppendLine($"{effectiveUrl} [ {method.ToUpper()} ] [ {httpVersion} {status} {message} ] [ {updateSize}({(outData != null ? outData.Length : 0)}) | {downloadSize}({(inData != null ? inData.Length : 0)}) ] [ {time * 1000} ms ]");

            if (outHeader != null)
            {
                sb.AppendLine("<b><color=lightblue>Request Headers</color></b>");
                foreach (var entry in outHeader)
                {
                    sb.AppendLine($"<b><color=silver>[{entry.Key}]</color></b> {entry.Value}");
                }
            }

            if (outData != null && outData.Length > 0)
            {
                sb.AppendLine($"<b><color=lightblue>Request Body</color></b> [ {outData.Length} ]");
                sb.AppendLine(Encoding.UTF8.GetString(outData, 0, Math.Min(outData.Length, 0x400)));
            }

            if (inHeader != null)
            {
                sb.AppendLine("<b><color=lightblue>Response Headers</color></b>");
                foreach (var entry in inHeader)
                {
                    sb.AppendLine($"<b><color=silver>[{entry.Key}]</color></b> {entry.Value}");
                }
            }

            if (inData != null && inData.Length > 0)
            {
                sb.AppendLine($"<b><color=lightblue>Response Body</color></b> [ {inData.Length} ]");
                sb.AppendLine(Encoding.UTF8.GetString(inData, 0, Math.Min(inData.Length, 0x400)));
            }

            Debug.Log(sb.ToString());
        }

        public Dictionary<string, string> GetAllRequestHeaders()
        {
            return userHeader;
        }

        public string GetRequestHeader(string key)
        {
            string value = null;
            if (userHeader != null)
            {
                userHeader.TryGetValue(key, out value);
            }
            return value;
        }

        public void SetHeader(string key, string value)
        {
            if (userHeader == null)
            {
                userHeader = new Dictionary<string, string>();
            }
            userHeader[key] = value;
        }

        public Dictionary<string, string> GetAllResponseHeaders()
        {
            return inHeader;
        }

        public string GetResponseHeader(string key)
        {
            inHeader.TryGetValue(key, out var value);
            return value;
        }

        [AOT.MonoPInvokeCallback(typeof(Delegates.HeaderFunction))]
        private static int HeaderFunction(IntPtr ptr, int size, int nmemb, IntPtr userdata)
        {
            unsafe
            {
                size = size * nmemb;
                var ums = new UnmanagedMemoryStream((byte*)ptr, size);
                var thiz = ((GCHandle)userdata).Target as CurlEasy;
                ums.CopyTo(thiz.responseHeaderStream);
                return size;
            }
        }

        [AOT.MonoPInvokeCallback(typeof(Delegates.WriteFunction))]
        private static int WriteFunction(IntPtr ptr, int size, int nmemb, IntPtr userdata)
        {
            unsafe
            {
                size = size * nmemb;
                var ums = new UnmanagedMemoryStream((byte*)ptr, size);
                var thiz = ((GCHandle)userdata).Target as CurlEasy;
                ums.CopyTo(thiz.responseBodyStream);
                return size;
            }
        }

        [AOT.MonoPInvokeCallback(typeof(Delegates.DebugFunction))]
        private static int DebugFunction(IntPtr ptr, CURLINFODEBUG type, IntPtr data, int size, IntPtr userdata)
        {
            if (type == CURLINFODEBUG.HEADER_OUT)
            {
                unsafe
                {
                    var ums = new UnmanagedMemoryStream((byte*)data, size);
                    var sr = new StreamReader(ums);

                    // Handle first line
                    {
                        var firstLine = sr.ReadLine();
                    }

                    while (true)
                    {
                        var line = sr.ReadLine();
                        if (!string.IsNullOrEmpty(line))
                        {
                            var index = line.IndexOf(':');
                            var thiz = ((GCHandle)userdata).Target as CurlEasy;
                            if (thiz.outHeader == null) thiz.outHeader = new Dictionary<string, string>();
                            var key = line.Substring(0, index).Trim();
                            var value = line.Substring(index + 1).Trim();
                            thiz.outHeader[key] = value;
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }
            return 0;
        }

        public string Escape(string data)
        {
            string result = null;
            var ptr = Lib.curl_easy_escape(easyPtr, data);
            if (ptr != IntPtr.Zero)
            {
                result = Marshal.PtrToStringAnsi(ptr);
                Lib.curl_free(ptr);
            }
            return result;
        }

        public string Unescape(string data)
        {
            string result = null;
            var ptr = Lib.curl_easy_unescape(easyPtr, data);
            if (ptr != IntPtr.Zero)
            {
                result = Marshal.PtrToStringAnsi(ptr);
                Lib.curl_free(ptr);
            }
            return result;
        }

        public static explicit operator IntPtr(CurlEasy easy)
        {
            return easy.easyPtr;
        }

        public static explicit operator CurlEasy(IntPtr ptr)
        {
            return new CurlEasy(ptr);
        }
    }
}