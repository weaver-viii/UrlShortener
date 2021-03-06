﻿module UrlShortener.Database

open System
open FSharp.Data.Sql
open FSharp.Data.Sql.Transactions
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open WebSharper
open WebSharper.AspNetCore
open UrlShortener.DataModel

type Sql = SqlDataProvider<
            // Connect to SQLite using System.Data.Sqlite.
            Common.DatabaseProviderTypes.SQLITE,
            SQLiteLibrary = Common.SQLiteLibrary.SystemDataSQLite,
            ResolutionPath = const(__SOURCE_DIRECTORY__ + "/../packages/System.Data.SQLite.Core/lib/netstandard2.0/"),
            // Store the database file in db/urlshortener.db.
            ConnectionString = const("Data Source=" + __SOURCE_DIRECTORY__ + "/db/urlshortener.db"),
            // Store the schema as JSON so that the compiler doesn't need the database to exist.
            ContextSchemaPath = const(__SOURCE_DIRECTORY__ + "/db/urlshortener.schema.json"),
            UseOptionTypes = true>

/// ASP.NET Core service that creates a new data context every time it's required.
type Context(config: IConfiguration, logger: ILogger<Context>) =
    do logger.LogInformation("Creating db context")

    let db =
        let connString = config.GetSection("ConnectionStrings").["UrlShortener"]
        Sql.GetDataContext(connString, TransactionOptions.Default)

    /// Apply all migrations.
    member this.Migrate() =
        try
            use ctx = db.CreateConnection()
            let evolve =
                new Evolve.Evolve(ctx, logger.LogInformation,
                    Locations = ["db/migrations"],
                    IsEraseDisabled = true)
            evolve.Migrate()
        with ex ->
            logger.LogCritical("Database migration failed: {0}", ex)

    /// Get the user for this Facebook user id, or create a user if there isn't one.
    member this.GetOrCreateFacebookUser(fbUserId: string, fbUserName: string) : Async<User.Id> = async {
        let existing =
            query { for u in db.Main.User do
                    where (u.FacebookId = fbUserId)
                    select (Some u.Id)
                    headOrDefault }
        match existing with
        | None ->
            let u =
                db.Main.User.Create(
                    Id = User.NewUserId(),
                    FacebookId = fbUserId,
                    FullName = fbUserName)
            do! db.SubmitUpdatesAsync()
            return u.Id
        | Some id ->
            return id
    }

    /// Get the user's full name.
    member this.GetUserData(userId: User.Id) : Async<User.Data option> = async {
        let name =
            query { for u in db.Main.User do
                    where (u.Id = userId)
                    select (Some u.FullName)
                    headOrDefault }
        return name |> Option.map (fun name ->
            {
                UserId = userId
                FullName = name
            } : User.Data
        )
    }

    /// Create a new link on this user's behalf, pointing to this url.
    /// Returns the slug for this new link.
    member this.CreateLink(userId: User.Id, url: string) : Async<Link.Slug> = async {
        let r =
            db.Main.Redirection.Create(
                Id = Link.NewLinkId(),
                CreatorId = userId,
                Url = url)
        do! db.SubmitUpdatesAsync()
        return Link.EncodeLinkId r.Id
    }

    /// Get the url pointed to by the given slug, if any,
    /// and increment its visit count.
    member this.TryVisitLink(slug: Link.Slug) : Async<string option> = async {
        match Link.TryDecodeLinkId slug with
        | None -> return None
        | Some linkId ->
            let link =
                query { for l in db.Main.Redirection do
                        where (l.Id = linkId)
                        select (Some l)
                        headOrDefault }
            match link with
            | None -> return None
            | Some link ->
                link.VisitCount <- link.VisitCount + 1L
                do! db.SubmitUpdatesAsync()
                return Some link.Url
    }

    /// Get data about all the links created by the given user.
    member this.GetAllUserLinks(userId: User.Id, ctx: Web.Context) : Async<Link.Data[]> = async {
        let links =
            query { for l in db.Main.Redirection do
                    where (l.CreatorId = userId)
                    select l }
        return links
            |> Seq.map (fun l ->
                let slug = Link.EncodeLinkId l.Id
                let url = Link.SlugToFullUrl ctx slug
                {
                    Slug = slug
                    LinkUrl = url
                    TargetUrl = l.Url
                    VisitCount = l.VisitCount
                } : Link.Data
            )
            |> Array.ofSeq
    }

    /// Check that this link belongs to this user, and if yes, delete it.
    member this.DeleteLink(userId: User.Id, slug: Link.Slug) : Async<unit> = async {
        match Link.TryDecodeLinkId slug with
        | None -> return ()
        | Some linkId ->
            let link =
                query { for l in db.Main.Redirection do
                        where (l.Id = linkId && l.CreatorId = userId)
                        select (Some l)
                        headOrDefault }
            match link with
            | None -> return ()
            | Some l ->
                l.Delete()
                return! db.SubmitUpdatesAsync()
    }

type Web.Context with
    /// Get a new database context.
    member this.Db : Context =
        this.HttpContext().RequestServices.GetRequiredService<Context>()
