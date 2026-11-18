#:package Microsoft.Data.Sqlite@9.0.0
// Génère shop.sqlite (jeu de test GranitDb : boutique en ligne).
// Usage (depuis la racine) : dotnet run samples/shop/build-shop.cs -- shop.sqlite samples/shop/schema.sql
// Les données sont déterministes (graine fixe) : relancer produit le même contenu.
using Microsoft.Data.Sqlite;

var output = Path.GetFullPath(args.Length > 0 ? args[0] : "shop.sqlite");
var schema = File.ReadAllText(args.Length > 1 ? args[1] : "samples/shop/schema.sql");
if (File.Exists(output)) File.Delete(output);

using var cn = new SqliteConnection($"Data Source={output};Pooling=False");
cn.Open();
void Exec(string sql, params (string, object?)[] p)
{
    using var c = cn.CreateCommand(); c.CommandText = sql;
    foreach (var (k, v) in p) c.Parameters.AddWithValue(k, v ?? DBNull.Value);
    c.ExecuteNonQuery();
}
Exec(schema);
using var tx = cn.BeginTransaction();
var rnd = new Random(42);
var start = new DateTime(2024, 1, 1);
string D(DateTime d) => d.ToString("yyyy-MM-dd HH:mm:ss");
string Ascii(string s) => s.ToLowerInvariant().Replace('é', 'e').Replace('è', 'e').Replace('ï', 'i').Replace('ç', 'c').Replace('ë', 'e');

var cats = new (int id, int? parent, string name)[] {
    (1,null,"Informatique"),(2,null,"Maison"),(3,null,"Sport"),(4,null,"Livres"),
    (5,1,"Ordinateurs"),(6,1,"Accessoires"),(7,2,"Cuisine"),(8,2,"Décoration"),(9,3,"Running"),(10,3,"Fitness")};
foreach (var c in cats)
    Exec("INSERT INTO categories VALUES ($i,$p,$n,$s)", ("$i", c.id), ("$p", c.parent), ("$n", c.name), ("$s", Ascii(c.name)));

string[] supNames = ["TechnoPlus", "MaisonDeluxe", "SportMax", "LibriFrance", "Globex Import", "Nordic Supply"];
string[] supCountry = ["FR", "FR", "DE", "FR", "CN", "SE"];
for (int i = 0; i < supNames.Length; i++)
    Exec("INSERT INTO suppliers VALUES ($i,$n,$e,$c)", ("$i", i + 1), ("$n", supNames[i]),
        ("$e", $"contact@{Ascii(supNames[i]).Replace(" ", "")}.example"), ("$c", supCountry[i]));

var products = new (string name, int cat, int sup, double price)[] {
    ("Portable 14 pouces",5,1,799),("Portable gamer 16 pouces",5,1,1499),("Mini PC bureau",5,1,449),
    ("Souris sans fil",6,1,24.9),("Clavier mécanique",6,1,89.9),("Écran 27 pouces",6,5,249),("Casque audio",6,5,129),("Hub USB-C",6,5,39.9),
    ("Poêle antiadhésive",7,2,29.9),("Robot pâtissier",7,2,349),("Set de couteaux",7,2,59.9),("Bouilloire inox",7,2,34.9),
    ("Cadre photo bois",8,2,19.9),("Lampe de bureau",8,6,45),("Plaid en laine",8,6,39),("Vase en céramique",8,6,27.5),
    ("Chaussures de running",9,3,119),("Montre GPS",9,3,229),("Gourde isotherme",9,3,22),("Brassard smartphone",9,3,14.9),
    ("Tapis de yoga",10,3,25),("Haltères 2x5 kg",10,3,49),("Corde à sauter",10,3,12.9),("Banc de musculation",10,3,189),
    ("Roman policier",4,4,8.9),("Livre de cuisine",4,4,24.5),("BD collector",4,4,32),("Guide de randonnée",4,4,16.9)};
for (int i = 0; i < products.Length; i++)
{
    var p = products[i];
    Exec("INSERT INTO products VALUES ($i,$sku,$n,$d,$c,$s,$p,$v,$a,$t)", ("$i", i + 1), ("$sku", $"SKU-{i + 1:0000}"),
        ("$n", p.name), ("$d", $"{p.name} — référence de démonstration."), ("$c", p.cat), ("$s", p.sup),
        ("$p", p.price), ("$v", p.cat == 4 ? 0.055 : 0.20), ("$a", i % 13 == 12 ? 0 : 1), ("$t", D(start.AddDays(rnd.Next(0, 60)))));
}

string[] whCity = ["Lille", "Lyon", "Marseille"];
for (int w = 0; w < 3; w++)
{
    Exec("INSERT INTO warehouses VALUES ($i,$n,$c)", ("$i", w + 1), ("$n", $"Entrepôt {whCity[w]}"), ("$c", whCity[w]));
    for (int p = 1; p <= products.Length; p++)
        Exec("INSERT INTO stock VALUES ($p,$w,$q)", ("$p", p), ("$w", w + 1), ("$q", rnd.Next(0, 150)));
}

string[] first = ["Camille","Lucas","Emma","Hugo","Léa","Louis","Chloé","Jules","Manon","Nathan","Sarah","Thomas","Inès","Paul","Zoé"];
string[] last = ["Martin","Bernard","Dubois","Thomas","Robert","Richard","Petit","Durand","Leroy","Moreau","Simon","Laurent"];
string[] cities = ["Paris","Lyon","Marseille","Toulouse","Lille","Nantes","Bordeaux","Strasbourg"];
string[] pc = ["75001","69001","13001","31000","59000","44000","33000","67000"];
int customers = 60, addrId = 0;
var addrOf = new Dictionary<int, int>();
for (int c = 1; c <= customers; c++)
{
    var fn = first[rnd.Next(first.Length)]; var ln = last[rnd.Next(last.Length)];
    Exec("INSERT INTO customers VALUES ($i,$e,$f,$l,$p,$b,$t)", ("$i", c), ("$e", $"{Ascii(fn)}.{Ascii(ln)}{c}@example.com"),
        ("$f", fn), ("$l", ln), ("$p", rnd.Next(3) == 0 ? null : $"06{rnd.Next(10000000, 99999999)}"),
        ("$b", $"{rnd.Next(1960, 2005)}-{rnd.Next(1, 13):00}-{rnd.Next(1, 29):00}"), ("$t", D(start.AddDays(rnd.Next(0, 300)))));
    int ci = rnd.Next(cities.Length);
    addrId++; addrOf[c] = addrId;
    Exec("INSERT INTO addresses VALUES ($i,$c,'shipping',$s,$p,$v,'FR')", ("$i", addrId), ("$c", c),
        ("$s", $"{rnd.Next(1, 99)} rue des Lilas"), ("$p", pc[ci]), ("$v", cities[ci]));
    if (rnd.Next(3) == 0)
    {
        addrId++;
        Exec("INSERT INTO addresses VALUES ($i,$c,'billing',$s,$p,$v,'FR')", ("$i", addrId), ("$c", c),
            ("$s", $"{rnd.Next(1, 99)} avenue de la Gare"), ("$p", pc[ci]), ("$v", cities[ci]));
    }
}

string[] statuses = ["pending", "paid", "shipped", "delivered", "delivered", "delivered", "cancelled"];
string[] methods = ["card", "card", "paypal", "transfer"];
int payId = 0;
for (int o = 1; o <= 150; o++)
{
    int cust = rnd.Next(1, customers + 1);
    var date = start.AddDays(rnd.Next(30, 330)).AddMinutes(rnd.Next(0, 1440));
    var status = statuses[rnd.Next(statuses.Length)];
    double total = 0;
    int lines = rnd.Next(1, 5);
    var used = new HashSet<int>();
    var items = new List<(int line, int prod, int qty, double price)>();
    for (int l = 1; l <= lines; l++)
    {
        int prod; do prod = rnd.Next(1, products.Length + 1); while (!used.Add(prod));
        int qty = rnd.Next(1, 4);
        items.Add((l, prod, qty, products[prod - 1].price));
        total += qty * products[prod - 1].price;
    }
    Exec("INSERT INTO orders VALUES ($i,$c,$s,$b,$st,$d,$t)", ("$i", o), ("$c", cust), ("$s", addrOf[cust]),
        ("$b", addrOf[cust]), ("$st", status), ("$d", D(date)), ("$t", Math.Round(total, 2)));
    foreach (var it in items)
        Exec("INSERT INTO order_items VALUES ($o,$l,$p,$q,$u)", ("$o", o), ("$l", it.line), ("$p", it.prod), ("$q", it.qty), ("$u", it.price));
    if (status != "pending" && status != "cancelled")
        Exec("INSERT INTO payments VALUES ($i,$o,$m,$a,$d)", ("$i", ++payId), ("$o", o), ("$m", methods[rnd.Next(methods.Length)]),
            ("$a", Math.Round(total, 2)), ("$d", D(date.AddMinutes(5))));
}

var seen = new HashSet<(int, int)>();
string[] comments = ["Très bien", "Conforme à la description", "Bon rapport qualité/prix", "Un peu cher", "Déçu", "Excellent !"];
for (int r = 1; r <= 120; r++)
{
    int p = rnd.Next(1, products.Length + 1), c = rnd.Next(1, customers + 1);
    if (!seen.Add((p, c))) continue;
    Exec("INSERT INTO reviews (product_id,customer_id,rating,comment,created_at) VALUES ($p,$c,$r,$m,$d)", ("$p", p), ("$c", c),
        ("$r", rnd.Next(1, 6)), ("$m", rnd.Next(4) == 0 ? null : comments[rnd.Next(comments.Length)]), ("$d", D(start.AddDays(rnd.Next(40, 360)))));
}
tx.Commit();
Console.WriteLine($"OK : {output}");
