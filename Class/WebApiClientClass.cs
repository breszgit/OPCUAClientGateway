using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;

namespace OPCUAClientGateway.Class
{
    public class WebApiClientClass
    {
        private readonly HttpClient client = new HttpClient();

        public dynamic CallWebApiwithObject(string url, object body){
            dynamic result = null;

            var jsdata = JsonSerializer.Serialize(body);
            var content = new StringContent(jsdata, Encoding.UTF8, "application/json"); 
            
            HttpResponseMessage response = null;
            Task.Run(() => {
                response = client.PostAsync(url, content).Result;
            }).Wait();
            
            var _resp = response.Content.ReadAsStringAsync();
            result = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(_resp.Result);

            return result;
        } 
    }
}