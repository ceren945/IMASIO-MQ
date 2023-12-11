using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using  IMQ;
using System;

namespace IMQ
{
    public class Program
    {
        public static void Main(string[] args)
        {
          
            
            // Web uygulamasini baslat
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                });
    }
}
