// SwaggerForge: OpenAPI document -> draft operation descriptors (one YAML per operation) + table schema.
// Phase 2. Output must pass DescriptorValidator before it is written (CLAUDE.md phase 2 acceptance).

if (args.Length < 2 || args.Contains("--help"))
{
    Console.WriteLine("""
        swaggerforge <openapi.json|yaml|url> --instance <ServiceInstance> [--out descriptors/<ServiceInstance>] [--prefix ext]

        Drafts one descriptor per operation and a table schema per resource. Drafts are reviewed by hand before approval.
        """);
    return args.Contains("--help") ? 0 : 1;
}

Console.Error.WriteLine("SwaggerForge is not implemented yet (phase 2). Port RESTVTP's SwaggerForge and emit descriptors per docs/descriptor-spec.md.");
return 2;
