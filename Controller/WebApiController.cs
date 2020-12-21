using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;

namespace Splicer_OPCUA.Controllers
{
    public class WebApiController
    {
        private readonly HttpClient client = new HttpClient();

        public dynamic CallWebApiwithObject(string url, object body){
            dynamic result = null;

            var jsdata = JsonSerializer.Serialize(body);
            var content = new StringContent(jsdata, Encoding.UTF8, "application/json"); 
            
            HttpResponseMessage response = null;
            Task.Run(() => {
                response = client.PostAsync(url, content).Result;
            });
            
            // var response = await client.PostAsync(url, content);

            return result;
        } 
    }
    // private static readonly HttpClient client = new HttpClient();
}