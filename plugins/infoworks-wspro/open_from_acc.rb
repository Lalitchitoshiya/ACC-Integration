# open_from_acc.rb — pull the latest model from ACC and load it into the OPEN network
#
# One-step "open from ACC" inside InfoWorks WS Pro:
#   1. Create a NEW EMPTY network in your database (right-click a Model Group ->
#      New -> Network) and OPEN it — this script writes into the current network.
#   2. Network -> Run Ruby Script... -> open_from_acc.rb
#   3. The latest ACC version (approved, else latest draft with a warning) is
#      downloaded and its rows are written into the open network.
#
# SAFETY: refuses to run if the open network already contains nodes/links, so it
# can never overwrite existing work. Always import into a fresh network.

require 'net/http'
require 'uri'
require 'json'

# ----------------------------- CONFIG ---------------------------------------
CONNECTOR_URL = 'http://localhost:5000'
USER_EMAIL    = 'modeler@demo.local'

# FALLBACK model, used ONLY when no version was picked on the dashboard
# (the "Open in WS Pro" button is the primary way to choose what to import —
# it works for any model with no script changes). Select by NAME; leave blank
# to print the catalog of available models and stop.
FALLBACK_MODEL_NAME = 'My INP Network'
# -----------------------------------------------------------------------------

def fail_out(msg)
  puts "OPEN FROM ACC FAILED: #{msg}"
  raise msg
end

net = WSApplication.current_network
fail_out('No network open — create a NEW EMPTY network, open it, then run this script.') if net.nil?

# Safety: only import into an empty network (never merge/overwrite silently).
%w[wn_node wn_pipe].each do |t|
  count = 0
  begin
    net.row_object_collection(t).each { |_| count += 1; break }
  rescue StandardError
    next
  end
  fail_out("Open network already has data in '#{t}' — open a new EMPTY network instead.") if count > 0
end

# --- 1. Resolve which version to import ---------------------------------------
def get(path, expect_redirect: false)
  uri = URI.parse(path.start_with?('http') ? path : "#{CONNECTOR_URL}#{path}")
  http = Net::HTTP.new(uri.host, uri.port)
  http.use_ssl = uri.scheme == 'https'
  http.read_timeout = 600
  req = Net::HTTP::Get.new(uri.request_uri)
  req['X-Dev-User'] = USER_EMAIL unless path.start_with?('http')
  res = http.request(req)
  return res['location'] if expect_redirect && res.code.to_i.between?(300, 399)
  fail_out("HTTP #{res.code} for #{uri.request_uri}: #{res.body[0..300]}") unless res.code.to_i == 200
  res
end

# Preferred path: a version picked on the dashboard ("Open in WS Pro" button).
# The pick is consumed on read — one click, one import. If nothing was picked,
# fall back to the latest approved version of MODEL_ID from the CONFIG above.
version = nil
pick = JSON.parse(get('/api/v1/wspro/pick').body)['pick']
if pick
  puts "Dashboard pick found: '#{pick['modelName']}' version #{pick['versionNumber']} (#{pick['reviewStatus']})"
  puts "WARNING: this version is not approved." unless pick['reviewStatus'] == 'Approved'
  version = { 'id' => pick['versionId'], 'versionNumber' => pick['versionNumber'],
              'reviewStatus' => pick['reviewStatus'], 'changeDescription' => pick['changeDescription'] }
else
  puts 'No dashboard pick pending — falling back to the configured FALLBACK_MODEL_NAME.'

  projects = JSON.parse(get('/api/v1/projects').body)['projects']
  if FALLBACK_MODEL_NAME.to_s.strip.empty?
    puts 'Available models (set FALLBACK_MODEL_NAME, or pick a version on the dashboard):'
    projects.each do |p|
      puts "  Project: #{p['name']}"
      p['models'].each { |m| puts "    - #{m['name']}" }
    end
    fail_out('No dashboard pick and FALLBACK_MODEL_NAME is blank.')
  end

  matches = projects.flat_map { |p| p['models'].select { |m| m['name'].casecmp?(FALLBACK_MODEL_NAME) } }
  fail_out("No model named '#{FALLBACK_MODEL_NAME}' — run with it blank to list options, or pick on the dashboard.") if matches.empty?
  fail_out("Model name '#{FALLBACK_MODEL_NAME}' exists in multiple projects — pick the version on the dashboard instead.") if matches.length > 1
  model_id = matches.first['id']
  puts "Fallback model: '#{matches.first['name']}'"

  latest = JSON.parse(get("/api/v1/models/#{model_id}/versions/latest-approved").body)
  version = latest['version']
  if version.nil?
    fail_out('Model has no versions in ACC yet.') if latest['fallback'].nil?
    version = latest['fallback']['version']
    puts "WARNING: importing latest #{latest['fallback']['reviewStatus'].upcase} version — not yet approved."
  end
end
puts "Fetching version #{version['versionNumber']} (#{version['reviewStatus']}) | #{version['changeDescription']}"

signed_url = get("/api/v1/versions/#{version['id']}/download", expect_redirect: true)
fail_out('No redirect to download URL.') if signed_url.nil?
csv_text = get(signed_url).body
puts "Downloaded #{csv_text.bytesize} bytes from ACC."

# --- 2. Parse the downloaded file (auto-detect format) ------------------------
# Two supported formats, normalized into the same `sections` structure:
#   a) WS Pro sectioned CSV (written by upload_to_acc.rb): "## table=NAME" markers
#   b) EPANET INP (raw uploads): [JUNCTIONS]/[TANKS]/[PIPES]/[COORDINATES]...
sections = {}

if csv_text.include?('## table=')
  current = nil
  csv_text.each_line do |raw|
    line = raw.chomp
    if line.start_with?('## table=')
      current = { 'fields' => nil, 'rows' => [] }
      sections[line.sub('## table=', '').strip] = current
    elsif current
      if current['fields'].nil?
        current['fields'] = line.split(',')
      elsif !line.empty?
        current['rows'] << line.split(',', -1)
      end
    end
  end

elsif csv_text.include?('[JUNCTIONS]') || csv_text.include?('[PIPES]')
  puts 'EPANET INP format detected — mapping to WS Pro tables.'
  # Pass 1: read INP sections into memory.
  inp = Hash.new { |h, k| h[k] = [] }
  section = nil
  csv_text.each_line do |raw|
    line = raw.split(';').first.to_s.strip
    next if line.empty?
    if line.start_with?('[')
      section = line.delete('[]').upcase
    elsif section
      inp[section] << line.split(/\s+/)
    end
  end
  coords = {}
  inp['COORDINATES'].each { |f| coords[f[0]] = [f[1], f[2]] if f.length >= 3 }

  # Pass 2: map INP element types onto WS Pro tables (EPANET tank -> wn_reservoir,
  # EPANET reservoir -> wn_fixed_head — WS Pro's naming, see specs/13).
  node_rows = ->(rows, with_z) do
    rows.map do |f|
      x, y = coords[f[0]] || ['', '']
      with_z ? [f[0], x, y, f[1] || ''] : [f[0], x, y]
    end
  end
  sections['wn_node'] = { 'fields' => %w[node_id x y z],
                          'rows' => node_rows.call(inp['JUNCTIONS'], true) }
  sections['wn_reservoir'] = { 'fields' => %w[node_id x y],
                               'rows' => node_rows.call(inp['TANKS'], false) }
  sections['wn_fixed_head'] = { 'fields' => %w[node_id x y],
                                'rows' => node_rows.call(inp['RESERVOIRS'], false) }
  sections['wn_pipe'] = { 'fields' => %w[us_node_id ds_node_id length diameter],
                          'rows' => inp['PIPES'].select { |f| f.length >= 3 }
                                               .map { |f| [f[1], f[2], f[3] || '', f[4] || ''] } }
  skipped_inp = (inp['PUMPS'].length + inp['VALVES'].length)
  puts "Note: #{skipped_inp} pump/valve link(s) not imported — INP pump/valve semantics don't map 1:1 to WS Pro tables (Phase 5 cross-tool exchange scope)." if skipped_inp > 0

else
  fail_out('Downloaded file is neither a WS Pro CSV export nor an EPANET INP — cannot import.')
end

total_rows = sections.values.sum { |s| s['rows'].length }
fail_out('Parsed 0 elements from the downloaded file — refusing to report an empty import as success.') if total_rows == 0

# --- 3. Write rows into the open network via the Exchange API ----------------
# Skip *_flag columns and internal fields that WS Pro derives itself; set what we
# can per row and count per-field failures instead of aborting the whole import.
SKIP_FIELD = lambda do |name|
  name == 'table' || name.end_with?('_flag') ||
    %w[hotlinks spatial_data bends triplets depth_volume_data landuse_areas
       customer_points alt_customer_points demand_by_category alt_demand_by_category
       land_use alt_land_use alt_total_connections].include?(name)
end

imported = 0
skipped_tables = []
field_errors = Hash.new(0)

net.transaction_begin
begin
  sections.each do |table_name, sec|
    next if sec['rows'].empty?
    roc = begin
      net.row_object_collection(table_name)
    rescue StandardError
      skipped_tables << table_name
      next
    end

    sec['rows'].each do |row|
      ro = begin
        roc.new_row_object(table_name)
      rescue StandardError
        begin
          net.new_row_object(table_name)
        rescue StandardError
          nil
        end
      end
      if ro.nil?
        skipped_tables << table_name unless skipped_tables.include?(table_name)
        break
      end

      sec['fields'].each_with_index do |fname, i|
        next if SKIP_FIELD.call(fname)
        val = row[i]
        next if val.nil? || val.empty?
        begin
          ro[fname] = val
        rescue StandardError
          field_errors["#{table_name}.#{fname}"] += 1
        end
      end
      begin
        ro.write
        imported += 1
      rescue StandardError => e
        field_errors["#{table_name}.<write>"] += 1
      end
    end
  end
  net.transaction_commit
rescue StandardError => e
  net.transaction_rollback rescue nil
  fail_out("Import aborted, transaction rolled back: #{e.message}")
end

puts ''
puts "SUCCESS: imported #{imported} rows from ACC version #{version['versionNumber']} into the open network."
puts "Skipped tables (not supported by this build): #{skipped_tables.uniq.join(', ')}" unless skipped_tables.empty?
unless field_errors.empty?
  puts 'Fields that could not be set (usually derived/read-only — review if unexpected):'
  field_errors.first(15).each { |k, v| puts "  #{k}: #{v} rows" }
end
puts 'Refresh the GeoPlan (or reopen the network) to see the imported model.'
