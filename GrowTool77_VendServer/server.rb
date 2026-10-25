require 'sinatra'
require 'json'
require 'sqlite3'

set :bind, '0.0.0.0'
set :port, 4567
set :show_exceptions, false

DB_PATH = File.join(__dir__, 'vends.db')

def db
  @db ||= begin
    d = SQLite3::Database.new(DB_PATH)
    d.results_as_hash = true
    d.execute_batch(<<~SQL)
      CREATE TABLE IF NOT EXISTS worlds (
        id         INTEGER PRIMARY KEY AUTOINCREMENT,
        name       TEXT NOT NULL UNIQUE,
        last_seen  TEXT NOT NULL
      );
      CREATE TABLE IF NOT EXISTS vends (
        id         INTEGER PRIMARY KEY AUTOINCREMENT,
        world_id   INTEGER NOT NULL REFERENCES worlds(id) ON DELETE CASCADE,
        tile_x     INTEGER NOT NULL,
        tile_y     INTEGER NOT NULL,
        item_id    INTEGER NOT NULL,
        item_name  TEXT,
        price_wl   INTEGER NOT NULL DEFAULT 0,
        stock      INTEGER NOT NULL DEFAULT 0,
        updated_at TEXT NOT NULL,
        UNIQUE(world_id, tile_x, tile_y)
      );
      CREATE INDEX IF NOT EXISTS idx_vends_item  ON vends(item_id);
      CREATE INDEX IF NOT EXISTS idx_vends_world ON vends(world_id);
    SQL
    d
  end
end

before { content_type 'application/json' }

get '/ping' do
  { status: 'ok', worlds: db.get_first_value('SELECT COUNT(*) FROM worlds') }.to_json
end

post '/worlds/:name/vends' do
  name = params[:name].upcase
  request.body.rewind
  body  = JSON.parse(request.body.read)
  vends = body['vends'] || []
  now   = Time.now.utc.iso8601

  db.execute('INSERT OR IGNORE INTO worlds (name, last_seen) VALUES (?, ?)', [name, now])
  db.execute('UPDATE worlds SET last_seen = ? WHERE name = ?', [now, name])
  world_id = db.get_first_value('SELECT id FROM worlds WHERE name = ?', name)

  # Delete ALL old vends for this world first — ensures stale entries are removed.
  # If vend had "dirt seed" last visit but now has "lava seed", dirt seed is gone.
  db.execute('DELETE FROM vends WHERE world_id = ?', world_id)

  insert_sql = 'INSERT INTO vends (world_id,tile_x,tile_y,item_id,item_name,price_wl,stock,updated_at) ' \
               'VALUES (?,?,?,?,?,?,?,?)'

  inserted = 0
  vends.each do |v|
    args = [world_id, v['x'].to_i, v['y'].to_i,
            v['item_id'].to_i, v['item_name'].to_s,
            v['price_wl'].to_i, v['stock'].to_i, now]
    db.execute(insert_sql, args)
    inserted += 1
  end

  { world: name, stored: inserted, replaced: true }.to_json
end

get '/worlds/:name/vends' do
  name = params[:name].upcase
  rows = db.execute(
    'SELECT v.tile_x,v.tile_y,v.item_id,v.item_name,v.price_wl,v.stock,v.updated_at ' \
    'FROM vends v JOIN worlds w ON w.id=v.world_id WHERE w.name=? ' \
    'ORDER BY v.price_wl ASC',
    name
  )
  { world: name, vends: rows }.to_json
end

get '/vends/search' do
  item_id   = params[:item_id]&.to_i
  item_name = params[:item_name]
  halt 400, { error: 'item_id or item_name required' }.to_json unless item_id || item_name

  if item_id
    rows = db.execute(
      'SELECT w.name AS world,v.* FROM vends v JOIN worlds w ON w.id=v.world_id ' \
      'WHERE v.item_id=? ORDER BY v.price_wl ASC LIMIT 50', item_id
    )
  else
    rows = db.execute(
      'SELECT w.name AS world,v.* FROM vends v JOIN worlds w ON w.id=v.world_id ' \
      "WHERE v.item_name LIKE ? ORDER BY v.price_wl ASC LIMIT 50", "%#{item_name}%"
    )
  end
  { results: rows, count: rows.size }.to_json
end

get '/worlds' do
  rows = db.execute(
    'SELECT w.name,w.last_seen,COUNT(v.id) AS vend_count ' \
    'FROM worlds w LEFT JOIN vends v ON v.world_id=w.id ' \
    'GROUP BY w.id ORDER BY w.last_seen DESC LIMIT 200'
  )
  { worlds: rows }.to_json
end

# Get ALL vend data for ALL worlds in one call — used by GrowTool77 on startup
# GET /vends/all
get '/vends/all' do
  rows = db.execute(
    'SELECT w.name AS world_name, v.tile_x, v.tile_y, v.item_id, v.item_name, ' \
    'v.price_wl, v.stock, v.updated_at ' \
    'FROM vends v JOIN worlds w ON w.id = v.world_id ' \
    'ORDER BY w.name, v.tile_x, v.tile_y'
  )

  # Group by world name
  worlds = {}
  rows.each do |r|
    name = r['world_name']
    worlds[name] ||= []
    worlds[name] << {
      'x'         => r['tile_x'],
      'y'         => r['tile_y'],
      'item_id'   => r['item_id'],
      'item_name' => r['item_name'],
      'price_wl'  => r['price_wl'],
      'stock'     => r['stock'],
    }
  end

  { worlds: worlds, world_count: worlds.size, vend_count: rows.size }.to_json
end

delete '/worlds/stale' do
  days   = (params[:days] || 30).to_i
  cutoff = (Time.now.utc - days * 86400).iso8601
  db.execute('DELETE FROM worlds WHERE last_seen < ?', cutoff)
  { deleted: db.changes }.to_json
end

puts "GrowTool77 Vend Server → http://localhost:4567"
puts "Database: #{DB_PATH}"
