- Initial migration. When DbContext is in a different assembly than startup, it is good to explicity specify the startup and target projects:
  `dotnet ef migrations add InitialCreate --project src/ArtificeWorks.Infrastructure --startup-project src/ArtificeWorks.Api --output-dir Persistence/Migrations`

- Apply Migration:
  - Local: `dotnet ef database update --project src/ArtificeWorks.Infrastructure --startup-project src/ArtificeWorks.Api`
  - Release: `dotnet ef migrations script --project src/ArtificeWorks.Infrastructure --startup-project src/ArtificeWorks.Api`
  - Note: SQL script is better for production deployements for record-keeping purposes

Docker Commands I keep forgetting:

- `docker compose up -d`
- `docker compose logs -f`
- `docker compose down`
- `docker compose down -v` [deletes volumes]

connect to postgresql db:

`docker exec -it artificeworks-postgres psql -U postgres -d artificeworks`
