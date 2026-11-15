output "db_host" {
  description = "The database endpoint."
  value       = aws_db_instance.orders.address
}

output "db_port" {
  value = aws_db_instance.orders.port
}

output "db_password" {
  value     = random_password.db.result
  sensitive = true
}

output "cache" {
  value = {
    endpoint = "${aws_elasticache_cluster.orders.cache_nodes[0].address}:6379"
    ttl      = var.cache_ttl
  }
}

output "queue_urls" {
  value = [aws_sqs_queue.orders.url, aws_sqs_queue.dlq.url]
}
