# upload_to_acc.rb — InfoWorks WS Pro → ACC Water Connector
#
# Runs inside InfoWorks WS Pro: Network menu -> Run Ruby Script -> select this file.
# Exports the currently open network to CSV and uploads it to the Connector API,
# which versions it into ACC Docs (specs/01-upload.md).
#
# Setup: edit the CONFIG block below once per machine/model.
# Requires: WS Pro's embedded Ruby (Exchange API); no gems needed.

require 'net/http'
require 'uri'
require 'securerandom'
require 'tmpdir'

# ----------------------------- CONFIG ---------------------------------------
CONNECTOR_URL = 'http://localhost:5000'
MODEL_ID      = '42ade9e2-621d-44f4-adbc-5b2b5cb922df' # "My INP Network" (GET /api/v1/projects/{id}/models)
USER_EMAIL    = 'modeler@demo.local'                    # dev auth header (X-Dev-User) until APS user login lands
SOURCE_TOOL   = 'InfoWorksWSPro'
# -----------------------------------------------------------------------------

def fail_out(msg)
  puts "UPLOAD FAILED: #{msg}"
  raise msg
end

net = WSApplication.current_network
fail_out('No network open — open a network before running this script.') if net.nil?

# --- 1. Ask for the change description (FR1.3: required, min 10 chars) -------
# Dialog support varies between WS Pro builds; try each mechanism in turn.
change_description = nil

# a) prompt grid (ICM-style)
begin
  layout = [['Change description (min 10 chars)', 'String', '']]
  result = WSApplication.prompt('Upload to ACC', layout, false)
  change_description = result && result[0]
rescue StandardError
  change_description = nil
end

# b) simple input box
if change_description.nil? || change_description.strip.length < 10
  begin
    if WSApplication.respond_to?(:input_box)
      change_description = WSApplication.input_box(
        'Change description for this upload (min 10 characters):',
        'Upload to ACC', '')
    end
  rescue StandardError
    change_description = nil
  end
end

# c) environment variable override (set before launching WS Pro)
if change_description.nil? || change_description.strip.length < 10
  change_description = ENV['ACC_CHANGE_DESCRIPTION']
end

# d) last resort: auto-description, so builds without any dialog support still work.
#    Edit DEFAULT_DESCRIPTION below per upload if your build lands here.
DEFAULT_DESCRIPTION = 'Uploaded from InfoWorks WS Pro via upload_to_acc.rb (no dialog support in this build)'
if change_description.nil? || change_description.strip.length < 10
  change_description = DEFAULT_DESCRIPTION
  puts 'NOTE: no input dialog available — using DEFAULT_DESCRIPTION from the script CONFIG.'
end

fail_out('A change description of at least 10 characters is required.') if change_description.nil? || change_description.strip.length < 10

# --- 2. Export the network to CSV --------------------------------------------
# Generic table walk (works regardless of WS Pro table naming): one CSV with a
# leading "table" column, all fields per row. The connector's metadata extractor
# consumes this shape (specs/13-metadata-schema.md).
export_path = File.join(Dir.tmpdir, "wspro_export_#{SecureRandom.hex(4)}.csv")
row_count = 0
File.open(export_path, 'w') do |f|
  net.tables.each do |table|
    fields = table.fields.map(&:name)
    f.puts "## table=#{table.name}"
    f.puts (['table'] + fields).join(',')
    net.row_object_collection(table.name).each do |row|
      values = fields.map do |fname|
        v = begin
              row[fname]
            rescue StandardError
              nil
            end
        v.nil? ? '' : v.to_s.gsub(',', ';').gsub(/\r?\n/, ' ')
      end
      f.puts ([table.name] + values).join(',')
      row_count += 1
    end
  end
end
puts "Exported #{row_count} rows to #{export_path}"

# --- 3. Multipart POST to the connector --------------------------------------
sw_version = begin
  WSApplication.version
rescue StandardError
  'unknown'
end

boundary = "----WsProAccUpload#{SecureRandom.hex(8)}"
file_bytes = File.binread(export_path)
file_name  = "#{File.basename(export_path)}"

body = +''
{ 'changeDescription' => change_description.strip,
  'sourceTool' => SOURCE_TOOL,
  'sourceToolVersion' => sw_version }.each do |k, v|
  body << "--#{boundary}\r\n"
  body << "Content-Disposition: form-data; name=\"#{k}\"\r\n\r\n#{v}\r\n"
end
body << "--#{boundary}\r\n"
body << "Content-Disposition: form-data; name=\"file\"; filename=\"#{file_name}\"\r\n"
body << "Content-Type: text/csv\r\n\r\n"
body << file_bytes
body << "\r\n--#{boundary}--\r\n"

uri = URI.parse("#{CONNECTOR_URL}/api/v1/models/#{MODEL_ID}/versions")
http = Net::HTTP.new(uri.host, uri.port)
http.use_ssl = uri.scheme == 'https'
http.read_timeout = 600 # large models

request = Net::HTTP::Post.new(uri.request_uri)
request['X-Dev-User'] = USER_EMAIL
request['Content-Type'] = "multipart/form-data; boundary=#{boundary}"
request.body = body

response = http.request(request)

if response.code.to_i == 201
  puts 'SUCCESS: model version uploaded to ACC.'
  puts response.body
else
  fail_out("Connector returned HTTP #{response.code}: #{response.body}")
end
