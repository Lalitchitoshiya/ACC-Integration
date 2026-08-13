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
MODEL_ID      = '42ade9e2-621d-44f4-adbc-5b2b5cb922df' # "My INP Network"
USER_EMAIL    = 'modeler@demo.local'
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

# --- 1. Resolve + download latest version from the connector ------------------
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

latest = JSON.parse(get("/api/v1/models/#{MODEL_ID}/versions/latest-approved").body)
version = latest['version']
if version.nil?
  fail_out('Model has no versions in ACC yet.') if latest['fallback'].nil?
  version = latest['fallback']['version']
  puts "WARNING: importing latest #{latest['fallback']['reviewStatus'].upcase} version — not yet approved."
end
puts "Fetching version #{version['versionNumber']} (#{version['reviewStatus']}) | #{version['changeDescription']}"

signed_url = get("/api/v1/versions/#{version['id']}/download", expect_redirect: true)
fail_out('No redirect to download URL.') if signed_url.nil?
csv_text = get(signed_url).body
puts "Downloaded #{csv_text.bytesize} bytes from ACC."

# --- 2. Parse the sectioned CSV ----------------------------------------------
# Format written by upload_to_acc.rb: "## table=NAME" / header row / data rows.
sections = {}
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
