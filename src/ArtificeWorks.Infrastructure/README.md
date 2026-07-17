## Migration Instructions

Start Local Infrastructure first:

```bash
docker compose up -d

```

If necessary `docker compose down -v` will stop all containers and delete volumes for a clean implementation.

### Steps to Clone

- Apply Migration:
  - Migration already exists in source control, so there's no need to add it.
  - Local: `dotnet ef database update --project src/ArtificeWorks.Infrastructure --startup-project src/ArtificeWorks.Api`
    - `database update` is fine for local development, but requires direct database access.
  - Release: `dotnet ef migrations script --project src/ArtificeWorks.Infrastructure --startup-project src/ArtificeWorks.Api`
    - Better for production. This will generate plain SQL script to be handled by CI/CD or equivalent deployment tool. No need for EF core tooling in production. Record-keeping on database architecture changes.

connect to postgresql db:

`docker exec -it artificeworks-postgres psql -U postgres -d artificeworks`
check volumes with `\dt`

### Development details. Included for enrichment and reference.

- Initial migration. When DbContext is in a different assembly than startup, it is good to explicity specify the startup and target projects:
  `dotnet ef migrations add InitialCreate --project src/ArtificeWorks.Infrastructure --startup-project src/ArtificeWorks.Api --output-dir Persistence/Migrations`
  - This is for reference only. The migration should be present in source control and only need be applied.
  - Gotcha: Connection string (in `../ArtificeWorks.Api`) specifies localhost by IP. IPv6 can resolve `localhost` to `::1` occasionally, which will error in Npgsql.
