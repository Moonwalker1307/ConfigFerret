resource "aws_sqs_queue" "orders" {
  name = "orders"
}

# Not an output block: output "x" is only matched at the start of a line.
