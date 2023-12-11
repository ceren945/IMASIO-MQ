using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using RabbitMQ.Client;
using System.Text;
using System.Security.Cryptography;
using Serilog;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using RabbitMQ.Client.Events;
using System.Net.Http;
using System;


namespace IMQ.Controllers
{

    [Route("api/[controller]")]
    [ApiController]
    public class SmsController : ControllerBase
    {
        private readonly ConnectionFactory _factory;
        private readonly IConnection _connection;
        private readonly IModel _channel;
        private readonly HttpClient _httpClient;

        public SmsController()
        {
            _factory = new ConnectionFactory
            {
                HostName = "localhost",
                UserName = "guest",
                Password = "guest",
                Port = 5672
            };

            // Serilog'u MongoDB'ye loglama 
            Log.Logger = new LoggerConfiguration()
                .WriteTo.MongoDB("mongodb://localhost:27017/IMASIO-MQ", collectionName: "Logs")
                .CreateLogger();

            // Bağlantı ve kanal oluşturma
            _connection = _factory.CreateConnection();
            _channel = _connection.CreateModel();
            _httpClient = new HttpClient();

            // Exchange ve kuyruk oluşturma
            string exchangeName = "sms_exchange";
            string exchangeType = ExchangeType.Direct;
            string queueName = "sms_queue";

            _channel.ExchangeDeclare(exchange: exchangeName, type: exchangeType);
            _channel.QueueDeclare(queue: queueName, durable: false, exclusive: false, autoDelete: false, arguments: null);

            // Kuyruğu exchange ile bağlama
            _channel.QueueBind(queue: queueName, exchange: exchangeName, routingKey: "sms_queue");

        }
        private string GenerateApiKey(string secretKey)
        {
            byte[] secretBytes = Encoding.UTF8.GetBytes(secretKey);

            using (var hmac = new HMACSHA256(secretBytes))
            {
                var hashedBytes = hmac.ComputeHash(secretBytes);
                return BitConverter.ToString(hashedBytes).Replace("-", "").ToLower();
            }

        }

        private bool ValidateApiKey(string apiKeyHeader, string generatedApiKey)
        {
            return apiKeyHeader == generatedApiKey;
        }

        [HttpPost("SendSmsToRabbitMQ")]
        public IActionResult SendSmsToRabbitMQ([FromBody] KeyModel keyModel)
        {
            try
            {
                if (keyModel == null || string.IsNullOrWhiteSpace(keyModel.key))
                {
                    Log.Warning("Geçersiz istek: Telefon numarası veya mesaj boş olamaz.");
                    return BadRequest("Telefon numarası ve mesaj boş olamaz");
                }

                // API anahtarı varlığı kontrolü
                if (!HttpContext.Request.Headers.TryGetValue("X-API-KEY", out var apiKeyHeader))
                {
                    Log.Warning("X-API-KEY başlığı bulunamadı.");
                    return StatusCode(401, "X-API-KEY başlığı bulunamadı. Yetkisiz erişim.");
                }

                //Api anahtarı doğrulama
                string secretKey = "skjvbfdsjk3094ı4ubrjejkfe"; // Özel bir anahtar olmalıdır.
                string generatedApiKey = GenerateApiKey(secretKey);

                if (!ValidateApiKey(apiKeyHeader, generatedApiKey))
                {
                    Log.Warning("API anahtarı doğrulanamadı.");
                    return StatusCode(403, "Geçersiz API anahtarı.");
                }

                byte[] body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(keyModel));
                _channel.BasicPublish(exchange: "sms_exchange", routingKey: "sms_queue", basicProperties: null, body: body);

                Log.Information($"SMS gönderme isteği başarıyla kuyruğa gönderildi");
                return Ok("SMS gönderme isteği başarıyla kuyruğa gönderildi");
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Hata: {ex.Message}");
                return BadRequest($"Hata: {ex.Message}");
            }
        }
      

        [HttpPost("ConsumeAndSendSmsToNetGsm")]
        public IActionResult ConsumeAndSendSmsToNetGsm()
        {
            string queueName = "sms_queue";
            var consumerEvent = new EventingBasicConsumer(_channel);
            bool isSmsSentSuccessfully = false;

            consumerEvent.Received += (ch, e) =>
            {
                try
                {
                    var byteArr = e.Body.ToArray();
                    var bodyStr = Encoding.UTF8.GetString(byteArr);
                    Console.WriteLine($"Mesaj içeriği: {bodyStr}");

                    if (string.IsNullOrEmpty(bodyStr))
                    {
                        Log.Error("Kuyruktan boş bir key alındı.");
                        Console.WriteLine("Kuyruktan boş bir mesaj alındı.");
                        return;
                    }

                    var keyModel = JsonConvert.DeserializeObject<KeyModel>(bodyStr);

                    if (keyModel == null || string.IsNullOrEmpty(keyModel.key))
                    {
                        Log.Warning("Geçersiz veya eksik anahtar.");
                        return;
                    }

                    string[] keyParts = keyModel.key.Split('|');

                    if (keyParts.Length != 3)
                    {
                        Log.Warning("Geçersiz veya eksik anahtar.");
                        return;
                    }

                    string appName = keyParts[0];
                    string apiKey = keyParts[1];

                    if (!IsValidApp(appName))
                    {
                        Log.Warning("Geçersiz uygulama adı.");
                        return;
                    }
                    if (!ValidateAppApiKey(appName, apiKey))
                    {
                        Log.Warning("FirmID doğrulanamdı");
                    } 

                   ValidateAppApiKey(appName, apiKey);

                    var smsModel = JsonConvert.DeserializeObject<SmsModel>(keyParts[2]);

                    if (smsModel == null)
                    {
                        Log.Error("Kuyruktan gelen JSON verisi geçersiz.");
                        return;
                    }

                    string message = smsModel.message ?? string.Empty;
                    string phoneNumber = smsModel.phoneNumber ?? string.Empty;

                    // Netgsm API'ye kuyruktan gelen SMS içeriğini gönder,servisten gelen yanıtı kontrol et
                    bool result = SendSmsToNetgsmAsync("usercode","password",message, phoneNumber).Result;

                    if (result)
                    {
                        Log.Information("SMS gönderme işlemi başarıyla tamamlandı.");
                        SendInfoToAnotherServiceAsync("RabbitMQ işlemi tamamlandı").Wait();
                    }
                    else
                    {
                        Log.Error("SMS gönderme işlemi başarısız oldu.");
                    }
                    isSmsSentSuccessfully = result;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Hata: {ErrorMessage}", ex.Message);
                    isSmsSentSuccessfully = false;
                }
            };

            _channel.BasicConsume(queueName, true, consumerEvent);

            Log.Information($"{queueName} kuyruğunu dinliyor...");

            // SMS gönderme işlemi başarılı ise yanıt dön
            if (isSmsSentSuccessfully)
            {
                return Ok("SMS işleme isteği başarıyla işlendi ve bilgi gönderildi.");
            }
            else
            {
                return BadRequest("SMS gönderme işlemi başarısız oldu.");
            }
        }

        private async Task<bool> SendSmsToNetgsmAsync(string usercode ,string password, string gsmno, string message)
        {
            try
            {
                bool success = false;

                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    using (var client = new HttpClient())
                    {
                        string netgsmApiUrl = "https://api.netgsm.com.tr/sms/send/get";

                        var formContent = new MultipartFormDataContent();
                        formContent.Add(new StringContent(usercode), "userrcode");
                        formContent.Add(new StringContent(password), "password");
                        formContent.Add(new StringContent(gsmno), "gsmno");
                        formContent.Add(new StringContent(message), "message");
                        formContent.Add(new StringContent("TR"), "dil");

                        HttpResponseMessage response = await client.PostAsync(netgsmApiUrl, formContent);

                        if (response.IsSuccessStatusCode)
                        {
                            string responseContent = await response.Content.ReadAsStringAsync();
                            Log.Information("Gönderdiğiniz SMS başarıyla netgsm servisine ulaştı. Yanıt: {ResponseContent}", responseContent);
                            success = true;
                            break; // Döngüden çık,başarılı
                        }
                        else
                        {
                            Log.Error($"Netgsm API'ye SMS gönderirken hata oluştu. HTTP durumu: {response.StatusCode}");

                            // Başarısızsa ve son deneme değilse kuyruğa ekleme yapma
                            if (attempt < 3)
                            {
                                _channel.BasicPublish(exchange: "sms_exchange", routingKey: "sms_queue", basicProperties: null, body: Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new KeyModel { key = gsmno + "|" + message })));
                                Log.Information($"SMS {attempt}. denemede gönderilemedi. Yeniden kuyruğa eklendi.");
                            }
                            else
                            {
                                Log.Error($"SMS gönderme işlemi {attempt}. denemede başarısız oldu. Kuyruğa ekleme yapılmadı.");
                                break;
                            }
                        }
                    }
                }

                return success; // başarılıysa true, değilse false 
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Netgsm API'ye SMS gönderirken hata oluştu: {ErrorMessage}", ex.Message);
                return false;
            }
        }

        private async Task SendInfoToAnotherServiceAsync(string message)
        {
            try
            {
                var requestData = new { Message = message };
                var jsonContent = new StringContent(JsonConvert.SerializeObject(requestData), Encoding.UTF8, "application/json");
                string serviceUrl = "https://bulutvet.com/api/b2b/validateKey";

                HttpResponseMessage response = await _httpClient.PostAsync(serviceUrl, jsonContent);

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    Log.Information($"Servise başarılı istek gönderildi. Yanıt: {responseContent}");
                }
                else
                {
                    Log.Error($"Servise istek gönderme hatası. HTTP durumu: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Servise istek gönderme hatası: {ErrorMessage}", ex.Message);
            }
        }

        private bool IsValidApp(string appName)
        {
            return appName == "CLNYO" || appName == "BLTVT";
        }

        private bool ValidateAppApiKey(string appName, string apiKey)
        {

            try
            {
                if (appName == "BLTVT")
                {
                    string validateApiKeyUrl = "https://bulutvet.com/api/b2b/validateKey";

                    var requestData = new { key = apiKey };
                    var jsonContent = new StringContent(JsonConvert.SerializeObject(requestData), Encoding.UTF8, "application/json");

                    HttpResponseMessage response = _httpClient.PostAsync(validateApiKeyUrl, jsonContent).Result;

                    if (response.IsSuccessStatusCode)
                    {
                        var responseContent = response.Content.ReadAsStringAsync().Result;
                        var result = JsonConvert.DeserializeAnonymousType(responseContent, new { Item1 = false, Item2 = "" });

                        if (result.Item1) //böyle bir FirmID var mı? itemtrue
                        {
                            if (result.Item2 == apiKey) //FirmID bizdekiyle eşleşiyo mu
                            {
                                Log.Information("FirmID doğrulandı. SMS gönderilmeye hazır.");
                                return true;
                            }
                            else
                            {
                                Log.Warning("FirmID doğrulanamadı. FirmID eşleşmiyor.");
                                return false;
                            }
                        }
                        else
                        {
                            Log.Warning("FirmID doğrulanamadı."); //item1 false
                            return false;
                        }
                    }
                    else
                    {
                        Log.Error($"Bulutvet API'ye FirmID doğrulama isteği gönderirken hata oluştu. HTTP durumu: {response.StatusCode}");
                        return false;
                    }
                }

                else if (appName == "CLNYO")
                {
                    string validateApiKeyUrl = "";

                    var requestData = new { key = apiKey };
                    var jsonContent = new StringContent(JsonConvert.SerializeObject(requestData), Encoding.UTF8, "application/json");

                    HttpResponseMessage response = _httpClient.PostAsync(validateApiKeyUrl, jsonContent).Result;

                    if (response.IsSuccessStatusCode)
                    {
                        var responseContent = response.Content.ReadAsStringAsync().Result;
                        var result = JsonConvert.DeserializeAnonymousType(responseContent, new { Item1 = false, Item2 = "" });

                        if (result.Item1) //böyle bir FirmID var mı? item true
                        {
                            if (result.Item2 == apiKey) //FirmID bizdekiyle eşleşiyor mu
                            {
                                Log.Information("FirmID doğrulandı. SMS gönderilmeye hazır.");
                                return true;
                            }
                            else
                            {
                                Log.Warning("FirmID doğrulanamadı. FirmID eşleşmiyor.");
                                return false;
                            }
                        }
                        else
                        {
                            Log.Warning("FirmID doğrulanamadı."); //item1 false
                            return false;
                        }
                    }
                    else
                    {
                        Log.Error($"CLNYO API'ye FirmID doğrulama isteği gönderirken hata oluştu. HTTP durumu: {response.StatusCode}");
                        return false;
                    }
                }

                else
                {
                    Log.Warning("Geçersiz uygulama ismi.");
                    return false;
                }

            }
            catch (Exception ex)
            {
                Log.Error(ex, "FirmID doğrulama hatası: {ErrorMessage}", ex.Message);
                return false;
            }
        }

        public class SmsModel
        {
            public string? message { get; set; }
            public string? phoneNumber { get; set; }
        }
        public class KeyModel
        {
            public string? key { get; set; }
        }

        ~SmsController()
        {
            _channel.Close();
            _connection.Close();
        }

    }
}