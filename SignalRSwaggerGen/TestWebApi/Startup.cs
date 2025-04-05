using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;

namespace TestWebApi
{
    internal class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllers();
            services.AddOpenApi("hubs", options =>
            {
                //var apiInfo = new OpenApiInfo { Title = "TestWebApi", Version = "v1" };
                options.AddDocumentTransformer((doc, ctx, ct) =>
                {
                    doc.Info.Title = "TestWebApi";
                    doc.Info.Version = "v1";
                    return Task.CompletedTask;
                });
                //options.SwaggerDoc("controllers", apiInfo);
                //options.SwaggerDoc("hubs", apiInfo);
                //options.IncludeXmlComments("TestWebApi.xml", true);
                options.AddSignalRSwaggerGen(o => o.UseXmlComments("TestWebApi.xml"));
            });
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                //app.UseSwagger();
                app.UseSwaggerUI(options =>
                {
                    options.SwaggerEndpoint("/api/openapi/hubs/openapi.json", "API V1");
                    //options.SwaggerEndpoint("/swagger/controllers/swagger.json", "REST API");
                    //options.SwaggerEndpoint("/swagger/hubs/swagger.json", "SignalR");
                });
            }

            app.UseHttpsRedirection();

            app.UseRouting();

            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapOpenApi("/api/openapi/{documentName}/openapi.json");
                endpoints.MapControllers();
            });
        }
    }
}
