# download_from_acc.rb — ACC Water Connector → InfoWorks WS Pro
#
# Companion to upload_to_acc.rb: fetches the latest APPROVED version of the model
# from ACC (falling back to latest draft, clearly labeled — specs/04 FR3.2) and
# saves it to a local folder ready for import/inspection in WS Pro.
#
# Run inside WS Pro (Network -> Run Ruby Script) or with any plain Ruby.

require 'net/http'
require 'uri'
require 'json'
require 'fileutils'

# ----------------------------- CONFIG ---------------------------------------
CONNECTOR_URL = 'http://localhost:5000'
MODEL_ID      = '42ade9e2-621d-44f4-adbc-5b2b5cb922df' # "My INP Network"
USER_EMAIL    = 'modeler@demo.local'
DOWNLOAD_DIR  = File.join(ENV['USERPROFILE'] || Dir.home, 'Downloads', 'acc-models')
# -----------------------------------------------------------------------------

def fail_out(msg)
  puts "DOWNLOAD FAILED: #{msg}"
  raise msg
end

def get_json(path)
  uri = URI.parse("#{CONNECTOR_URL}#{path}")
  http = Net::HTTP.new(uri.host, uri.port)
  http.use_ssl = uri.scheme == 'https'
  req = Net::HTTP::Get.new(uri.request_uri)
  req['X-Dev-User'] = USER_EMAIL
  res = http.request(req)
  fail_out("Connector returned HTTP #{res.code} for #{path}: #{res.body}") unless res.code.to_i == 200
  JSON.parse(res.body)
end

# --- 1. Resolve which version to fetch (latest approved, else flagged fallback) ---
latest = get_json("/api/v1/models/#{MODEL_ID}/versions/latest-approved")
version = latest['version']
if version.nil?
  fallback = latest['fallback']
  fail_out('Model has no versions at all — upload one first.') if fallback.nil?
  version = fallback['version']
  puts "WARNING: no APPROVED version exists yet — downloading latest #{fallback['reviewStatus'].upcase} version instead."
  puts 'Treat this as work-in-progress, not the authoritative model.'
else
  puts 'Latest APPROVED version found.'
end

puts "Version #{version['versionNumber']} | uploaded #{version['uploadedAt']} | #{version['changeDescription']}"

# --- 2. Follow the connector's redirect to the signed ACC download URL -------
uri = URI.parse("#{CONNECTOR_URL}/api/v1/versions/#{version['id']}/download")
http = Net::HTTP.new(uri.host, uri.port)
req = Net::HTTP::Get.new(uri.request_uri)
req['X-Dev-User'] = USER_EMAIL
res = http.request(req)
fail_out("Expected redirect, got HTTP #{res.code}: #{res.body}") unless res.code.to_i.between?(300, 399)
signed_url = res['location'] || fail_out('Redirect had no Location header.')

# --- 3. Download the file bytes from Autodesk cloud ---------------------------
signed = URI.parse(signed_url)
dl = Net::HTTP.new(signed.host, signed.port)
dl.use_ssl = signed.scheme == 'https'
dl.read_timeout = 600
file_res = dl.request(Net::HTTP::Get.new(signed.request_uri))
fail_out("Cloud download failed: HTTP #{file_res.code}") unless file_res.code.to_i == 200

FileUtils.mkdir_p(DOWNLOAD_DIR)
status_tag = version['reviewStatus'].to_s.downcase
out_path = File.join(DOWNLOAD_DIR, "model_v#{version['versionNumber']}_#{status_tag}.csv")
File.binwrite(out_path, file_res.body)

puts "SUCCESS: saved version #{version['versionNumber']} (#{version['reviewStatus']}) to:"
puts "  #{out_path}"
puts ''
puts 'Next: import into WS Pro via the Open Data Import Centre, or inspect the CSV directly.'
if version['metadata'] && version['metadata']['network']
  n = version['metadata']['network']
  puts "Contents: #{n['nodes']['total']} nodes, #{n['links']['total']} links, #{n['totalPipeLength'].round(1)} m of pipe."
end
