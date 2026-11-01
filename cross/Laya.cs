using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

namespace CapriDesktop
{
    // Her hands, when they turn them on: Convai's Laya, a decision model that runs on their own PC through laya-serve. It
    // can't see, and it writes nothing: it reads the job and her brain's order, and picks one of the steps her brain offered.
    // So her brain gives the orders, Laya picks the action, and Capri carries it out under the same rules as ever.
    internal static class Laya
    {
        internal const string Address = "http://127.0.0.1:8890";
        internal const int MostOptions = 6;
        internal static int Patience = 8000;
        // Where laya-serve is, without a path, or null when her brain's own first step is used.
        internal static volatile string Url;

        internal static string Clean(string typed)
        {
            string url = (typed ?? "").Trim().TrimEnd('/');
            if (url.Length == 0) return null;
            if (url.IndexOf("://", StringComparison.Ordinal) < 0) url = "http://" + url;
            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed) || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) || parsed.Host.Length == 0) return null;
            return parsed.Scheme + "://" + parsed.Authority;
        }

        // Whether laya-serve answers, for her menus.
        internal static bool Answers(string url)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url + "/health");
                request.Timeout = 1500;
                request.Proxy = null;
                using (WebResponse response = request.GetResponse()) return true;
            }
            catch (Exception) { return false; }
        }

        // The question it gets: the job, the order and the steps so far as its state, with one letter for each step on offer.
        internal static string Question(string job, string order, IList<string> done, IList<string> offered)
        {
            StringBuilder body = new StringBuilder("{\"state\":{\"job\":").Append(Voice.Quote(job ?? "")).Append(",\"order\":").Append(Voice.Quote(order ?? "")).Append(",\"steps_so_far\":[");
            for (int i = 0; i < done.Count; i++) body.Append(i > 0 ? "," : "").Append(Voice.Quote(done[i]));
            body.Append("]},\"questions\":{\"step\":{\"type\":\"choice\",\"instructions\":\"Which single step carries out the order right now?\",\"criteria\":{");
            for (int i = 0; i < offered.Count; i++) body.Append(i > 0 ? "," : "").Append('"').Append((char)('a' + i)).Append("\":").Append(Voice.Quote(offered[i]));
            return body.Append("}}}}").ToString();
        }

        // The number of the step it picks. Null with the reason in trouble when it can't say, so her brain's first step goes ahead.
        internal static int? Pick(string url, string job, string order, IList<string> done, IList<string> offered, out string trouble)
        {
            trouble = null;
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url + "/v1/systemone");
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Proxy = null;
                request.Timeout = Patience;
                request.ReadWriteTimeout = Patience;
                byte[] payload = Encoding.UTF8.GetBytes(Question(job, order, done, offered));
                using (Stream upload = request.GetRequestStream()) upload.Write(payload, 0, payload.Length);
                using (WebResponse response = request.GetResponse())
                using (JsonDocument reply = JsonDocument.Parse(response.GetResponseStream()))
                {
                    string letter = reply.RootElement.GetProperty("answers").GetProperty("step").GetProperty("choice").GetString();
                    int picked = string.IsNullOrEmpty(letter) ? -1 : letter[0] - 'a';
                    if (picked >= 0 && picked < offered.Count) return picked;
                    trouble = "Laya picked something that wasn't on offer.";
                }
            }
            catch (WebException error)
            {
                HttpWebResponse response = error.Response as HttpWebResponse;
                trouble = response != null ? "Laya said HTTP " + (int)response.StatusCode + "." : "Can't reach Laya at " + url + ". Is it running?";
                if (response != null) response.Close();
            }
            catch (Exception error) { trouble = Voice.Short("Laya glitched: " + error.Message); }
            return null;
        }
    }
}
